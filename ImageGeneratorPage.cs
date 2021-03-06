#define LEGACY_STYLES

using Maps.Rendering;
using Maps.Serialization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Mime;
using System.Web;
using System.Xml.Serialization;

namespace Maps.Pages
{
    public abstract class ImageGeneratorPage : BasePage
    {
        public override string DefaultContentType { get { return Util.MediaTypeName_Image_Png; } }

        public const double MinScale = 0.0078125; // Math.Pow(2, -7);
        public const double MaxScale = 512; // Math.Pow(2, 9);

        protected void ParseOptions(ref MapOptions options, ref Stylesheet.Style style)
        {
            options = (MapOptions)GetIntOption("options", (int)options);

#if LEGACY_STYLES
            // Handle deprecated/legacy options bits for selecting style
            style =
                (options & MapOptions.StyleMaskDeprecated) == MapOptions.PrintStyleDeprecated ? Stylesheet.Style.Atlas :
                (options & MapOptions.StyleMaskDeprecated) == MapOptions.CandyStyleDeprecated ? Stylesheet.Style.Candy :
                Stylesheet.Style.Poster;
#endif // LEGACY_STYLES

            if (HasOption("style"))
            {
                switch (GetStringOption("style").ToLowerInvariant())
                {
                    case "poster": style = Stylesheet.Style.Poster; break;
                    case "atlas": style = Stylesheet.Style.Atlas; break;
                    case "print": style = Stylesheet.Style.Print; break;
                    case "candy": style = Stylesheet.Style.Candy; break;
                }
            }
        }

        protected void SetCommonResponseHeaders()
        {
            // CORS - allow from any origin
            Response.AddHeader("Access-Control-Allow-Origin", "*");
#if !DEBUG
            Response.Cache.SetCacheability(HttpCacheability.Public);
            Response.Cache.SetExpires(DateTime.Now.AddHours(12));
            Response.Cache.SetValidUntilExpires(true);
#endif
            if (Request.HttpMethod == "POST")
            {
                Response.Cache.SetCacheability(HttpCacheability.NoCache);
            }
        }

        protected void ProduceResponse(string title, Render.RenderContext ctx, Size tileSize,
            int rot = 0, float translateX = 0, float translateY = 0,
            bool transparent = false)
        {
            // New-style Options
            // TODO: move to ParseOptions (maybe - requires options to be parsed after stylesheet creation?)
            if (GetBoolOption("sscoords", defaultValue: false))
            {
                ctx.styles.hexCoordinateStyle = Stylesheet.HexCoordinateStyle.Subsector;
            }

            if (!GetBoolOption("routes", defaultValue: true))
            {
                ctx.styles.macroRoutes.visible = false;
                ctx.styles.microRoutes.visible = false;
            }

            double devicePixelRatio = GetDoubleOption("dpr", 1);

            if (Accepts(MediaTypeNames.Application.Pdf))
            {
                using (var document = new PdfDocument())
                {
                    document.Version = 14; // 1.4 for opacity
                    document.Info.Title = title;
                    document.Info.Author = "Joshua Bell";
                    document.Info.Creator = "TravellerMap.com";
                    document.Info.Subject = DateTime.Now.ToString("F", CultureInfo.InvariantCulture);
                    document.Info.Keywords = "The Traveller game in all forms is owned by Far Future Enterprises. Copyright (C) 1977 - 2013 Far Future Enterprises. Traveller is a registered trademark of Far Future Enterprises.";

                    // TODO: Credits/Copyright
                    // This is close, but doesn't define the namespace correctly: 
                    // document.Info.Elements.Add( new KeyValuePair<string, PdfItem>( "/photoshop/Copyright", new PdfString( "HelloWorld" ) ) );

                    PdfPage page = document.AddPage();

                    // NOTE: only PageUnit currently supported in XGraphics is Points
                    page.Width = XUnit.FromPoint(tileSize.Width);
                    page.Height = XUnit.FromPoint(tileSize.Height);

                    PdfSharp.Drawing.XGraphics gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);

                    RenderToGraphics(ctx, rot, translateX, translateY, gfx);

                    using (var stream = new MemoryStream())
                    {
                        document.Save(stream, closeStream: false);

                        SetCommonResponseHeaders();

                        Response.ContentType = MediaTypeNames.Application.Pdf;
                        Response.AddHeader("content-length", stream.Length.ToString());
                        Response.AddHeader("content-disposition", "inline;filename=\"map.pdf\"");
                        Response.BinaryWrite(stream.ToArray());
                        Response.Flush();
                        stream.Close();
                    }

                    return;
                }
            }

            using (var bitmap = new Bitmap((int)Math.Floor(tileSize.Width * devicePixelRatio), (int)Math.Floor(tileSize.Height * devicePixelRatio), PixelFormat.Format32bppArgb))
            {
                if (transparent)
                {
                    bitmap.MakeTransparent();
                }

                using (var g = Graphics.FromImage(bitmap))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                    using (var graphics = XGraphics.FromGraphics(g, new XSize(tileSize.Width * devicePixelRatio, tileSize.Height * devicePixelRatio)))
                    {
                        if (devicePixelRatio != 0)
                        {
                            graphics.ScaleTransform(devicePixelRatio);
                        }

                        RenderToGraphics(ctx, rot, translateX, translateY, graphics);
                    }
                }

                SetCommonResponseHeaders();
                BitmapResponse(ctx.styles, bitmap, transparent ? Util.MediaTypeName_Image_Png : null);
            }
        }

        private static void RenderToGraphics(Render.RenderContext ctx, int rot, float translateX, float translateY, XGraphics graphics)
        {
            graphics.TranslateTransform(translateX, translateY);
            graphics.RotateTransform(rot * 90);

            using (Maps.Rendering.RenderUtil.SaveState(graphics))
            {

                if (ctx.clipPath != null)
                {
                    XMatrix m = ctx.ImageSpaceToWorldSpace;
                    graphics.MultiplyTransform(m);
                    graphics.IntersectClip(ctx.clipPath);
                    m.Invert();
                    graphics.MultiplyTransform(m);
                }

                ctx.graphics = graphics;
                Maps.Rendering.Render.RenderTile(ctx);
            }


            if (ctx.border && ctx.clipPath != null)
            {
                using (Maps.Rendering.RenderUtil.SaveState(graphics))
                {
                    // Render border in world space
                    XMatrix m = ctx.ImageSpaceToWorldSpace;
                    graphics.MultiplyTransform(m);
                    XPen pen = new XPen(ctx.styles.imageBorderColor, 0.2f);

                    // PdfSharp can't ExcludeClip so we take advantage of the fact that we know 
                    // the path starts on the left edge and proceeds clockwise. We extend the 
                    // path with a counterclockwise border around it, then use that to exclude 
                    // the original path's region for rendering the border.
                    ctx.clipPath.Flatten();
                    RectangleF bounds = PathUtil.Bounds(ctx.clipPath);
                    bounds.Inflate(2 * (float)pen.Width, 2 * (float)pen.Width);
                    List<byte> types = new List<byte>(ctx.clipPath.Internals.GdiPath.PathTypes);
                    List<PointF> points = new List<PointF>(ctx.clipPath.Internals.GdiPath.PathPoints);

                    PointF key = points[0];
                    points.Add(new PointF(bounds.Left, key.Y)); types.Add(1);
                    points.Add(new PointF(bounds.Left, bounds.Bottom)); types.Add(1);
                    points.Add(new PointF(bounds.Right, bounds.Bottom)); types.Add(1);
                    points.Add(new PointF(bounds.Right, bounds.Top)); types.Add(1);
                    points.Add(new PointF(bounds.Left, bounds.Top)); types.Add(1);
                    points.Add(new PointF(bounds.Left, key.Y)); types.Add(1);
                    points.Add(new PointF(key.X, key.Y)); types.Add(1);

                    XGraphicsPath path = new XGraphicsPath(points.ToArray(), types.ToArray(), XFillMode.Winding);
                    graphics.IntersectClip(path);
                    graphics.DrawPath(pen, ctx.clipPath);
                }
            }
        }


        protected void BitmapResponse(Stylesheet styles, Bitmap bitmap, string mimeType)
        {
            // JPEG or PNG if not specified, based on style
            if (mimeType == null)
            {
                mimeType = styles.preferredMimeType;
            }

            Response.ContentType = mimeType;

            // Searching for a matching encoder
            ImageCodecInfo encoder = null;
            ImageCodecInfo[] encoders = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < encoders.Length; ++i)
            {
                if (encoders[i].MimeType == Response.ContentType)
                {
                    encoder = encoders[i];
                    break;
                }
            }

            if (encoder != null)
            {
                EncoderParameters encoderParams;
                if (mimeType == MediaTypeNames.Image.Jpeg)
                {
                    encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)95);
                }
                else if (mimeType == Util.MediaTypeName_Image_Png)
                {
                    encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.ColorDepth, 8);
                }
                else
                {
                    encoderParams = new EncoderParameters(0);
                }

                if (mimeType == Util.MediaTypeName_Image_Png)
                {
                    // PNG encoder is picky about streams - need to do an indirection
                    // http://www.west-wind.com/WebLog/posts/8230.aspx
                    using (var ms = new MemoryStream())
                    {
                        bitmap.Save(ms, encoder, encoderParams);
                        ms.WriteTo(Context.Response.OutputStream);
                    }
                }
                else
                {
                    bitmap.Save(Context.Response.OutputStream, encoder, encoderParams);
                }

                encoderParams.Dispose();
            }
            else
            {
                // Default to GIF if we can't find anything
                Response.ContentType = MediaTypeNames.Image.Gif;
                bitmap.Save(Context.Response.OutputStream, ImageFormat.Gif);
            }
        }

        protected Sector GetPostedSector()
        {
            Sector sector = null;
            if (Request.ContentType.StartsWith("multipart/form-data")
                && Request.Files["file"] != null && Request.Files["file"].ContentLength > 0)
            {
                HttpPostedFile hpf = Request.Files["file"];
                sector = new Sector(hpf.InputStream, hpf.ContentType);

                if (Request.Files["metadata"] != null && Request.Files["metadata"].ContentLength > 0)
                {
                    hpf = Request.Files["metadata"];

                    string type = SectorMetadataFileParser.SniffType(hpf.InputStream);
                    Sector meta = SectorMetadataFileParser.ForType(type).Parse(hpf.InputStream);
                    sector.Merge(meta);
                }
            }
            else if (Request.ContentType == "application/x-www-form-urlencoded"
                    && Request.Form["data"] != null)
            {
                string data = Request.Form["data"];
                sector = new Sector(data.ToStream(), MediaTypeNames.Text.Plain);

                if (!String.IsNullOrEmpty(Request.Form["metadata"]))
                {
                    string metadata = Request.Form["metadata"];
                    string type = SectorMetadataFileParser.SniffType(metadata.ToStream());
                    var parser = SectorMetadataFileParser.ForType(type);
                    using (var reader = new StringReader(metadata))
                    {
                        Sector meta = parser.Parse(reader);
                        sector.Merge(meta);
                    }
                }
            }
            else if (Request.ContentType == MediaTypeNames.Text.Plain)
            {
                sector = new Sector(Request.InputStream, MediaTypeNames.Text.Plain);
            }
            return sector;
        }
    }
}
