using ProsperoThemes.App.Library;
using ProsperoThemes.App.Ui;
using SharpProspero.Graphics;
using SharpProspero.Ui;
using Xunit;

namespace ProsperoThemes.Shared.Tests;

/// <summary>Theme tokens, antialiased Paint shapes, the image cache, button glyph sprites and text wrapping.</summary>
public unsafe class UiFoundationTests
{
    private static readonly Color Bg = Color.FromRgb(16, 24, 32);
    private static readonly Color Fg = Color.FromRgb(240, 244, 250);

    // A canvas cleared to an opaque colour, the way the console's back buffer is.
    private static PixelBuffer Canvas(int width, int height, Color? background = null)
    {
        var buffer = new PixelBuffer(width, height);
        buffer.Clear(background ?? Bg);
        return buffer;
    }

    private static uint Px(PixelBuffer b, int x, int y) => b.AsSurface().Pixels[y * b.Width + x];
    private static int Red(uint p) => (int)((p >> 16) & 0xFF);
    private static int Alpha(uint p) => (int)(p >> 24);

    // How much of a shape a pixel carries, 0 to 1, judged on the red channel between the background and the colour.
    private static float Coverage(uint pixel, Color background, Color color) =>
        (Red(pixel) - background.R) / (float)(color.R - background.R);

    private static AtlasFont Font(string name = "inter-28.p5font") =>
        new(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)));

    // ---- Theme ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Theme_keeps_the_established_palette_and_the_grid()
    {
        Assert.Equal(Color.FromRgb(7, 12, 21).Value, Theme.Ink.Value);
        Assert.Equal(Color.FromRgb(242, 246, 253).Value, Theme.White.Value);
        Assert.Equal(Color.FromRgb(154, 169, 190).Value, Theme.Muted.Value);
        Assert.Equal(Color.FromRgb(155, 209, 255).Value, Theme.Accent.Value);
        Assert.Equal(Color.FromRgb(12, 18, 28).Value, Theme.PillInk.Value);
        Assert.Equal(Color.FromRgb(255, 190, 140).Value, Theme.Warn.Value);
        Assert.Equal(Color.FromRgb(30, 40, 54).Value, Theme.KeyFill.Value);
        Assert.Equal(Theme.Ink.R, Theme.Panel.R);
        Assert.InRange((int)Theme.Panel.A, 190, 220);
        Assert.Equal(96, Theme.SafeX);
        foreach (int multiple in new[] { Theme.SafeX, Theme.SafeTop, Theme.SafeBottom, Theme.ContentTop, Theme.ContentBottom, Theme.FooterY })
            Assert.Equal(0, multiple % Theme.Unit);
        Assert.True(Theme.ContentTop < Theme.ContentBottom && Theme.ContentBottom < Theme.FooterY);
        Assert.Equal(Theme.ScreenHeight - Theme.SafeBottom, Theme.FooterY + Theme.FooterHeight);
        Assert.Equal(16, Theme.RadiusCard);
        Assert.Equal(20, Theme.RadiusPanel);
        Assert.Equal(32, Theme.RadiusPill(64));
    }

    [Fact]
    public void Every_atlas_of_the_type_scale_ships_and_loads()
    {
        var styles = new[] { Theme.TypeScale.Hero, Theme.TypeScale.Heading, Theme.TypeScale.Section, Theme.TypeScale.Body, Theme.TypeScale.Label, Theme.TypeScale.Caption };
        Assert.Equal(styles.Length, styles.Select(s => s.File).Distinct().Count());
        foreach (var style in styles)
        {
            var font = Font(style.File);
            Assert.True(font.HasGlyph('A') && font.HasGlyph('…') && font.HasGlyph('·'), style.Name);
            // The pixel size is the atlas's em size, so a larger step is a taller line.
            Assert.InRange(font.LineHeight, style.Size, style.Size * 2);
        }
        Assert.True(Font("inter-48.p5font").LineHeight > Font("inter-36.p5font").LineHeight);
        Assert.NotEqual(Font("inter-28.p5font").MeasureText("Library"), Font("inter-28-semibold.p5font").MeasureText("Library"));   // really the heavier face
    }

    // ---- Paint: rounded rectangles ----------------------------------------------------------------------------------

    [Fact]
    public void Rounded_rect_has_an_opaque_interior_and_blended_corners()
    {
        using var canvas = Canvas(64, 64);
        var s = canvas.AsSurface();
        const int x = 8, y = 10, w = 40, h = 30, r = 12;
        Paint.FillRoundedRect(s, x, y, w, h, r, Fg);

        int partial = 0;
        for (int py = y; py < y + h; py++)
            for (int px = x; px < x + w; px++)
            {
                bool cornerSquare = (px < x + r || px >= x + w - r) && (py < y + r || py >= y + h - r);
                uint p = Px(canvas, px, py);
                if (!cornerSquare) { Assert.Equal(Fg.Value, p); continue; }
                float c = Coverage(p, Bg, Fg);
                if (c > 0.02f && c < 0.98f) partial++;
            }
        Assert.True(partial >= 4 * r, $"expected antialiased edge pixels around the arcs, got {partial}");
        Assert.Equal(Bg.Value, Px(canvas, x, y));                 // the very corner stays background
        Assert.Equal(Fg.Value, Px(canvas, x + r, y + r));         // the arc's centre is solid
        // Symmetric: the same pixel at all four corners.
        for (int j = 0; j < r; j++)
            for (int i = 0; i < r; i++)
            {
                uint tl = Px(canvas, x + i, y + j);
                Assert.Equal(tl, Px(canvas, x + w - 1 - i, y + j));
                Assert.Equal(tl, Px(canvas, x + i, y + h - 1 - j));
                Assert.Equal(tl, Px(canvas, x + w - 1 - i, y + h - 1 - j));
            }
    }

    [Fact]
    public void Rounded_rect_draws_nothing_outside_its_bounds()
    {
        using var canvas = Canvas(64, 64);
        Paint.FillRoundedRect(canvas.AsSurface(), 10, 12, 30, 20, 8, Fg);
        Paint.StrokeRoundedRect(canvas.AsSurface(), 44, 40, 16, 16, 6, 2, Fg);
        for (int py = 0; py < 64; py++)
            for (int px = 0; px < 64; px++)
            {
                bool inFill = px is >= 10 and < 40 && py is >= 12 and < 32;
                bool inStroke = px is >= 44 and < 60 && py is >= 40 and < 56;
                if (!inFill && !inStroke) Assert.Equal(Bg.Value, Px(canvas, px, py));
            }
    }

    [Fact]
    public void Translucent_rounded_rect_has_no_seams_between_its_spans()
    {
        using var canvas = Canvas(80, 80);
        var color = Fg.WithAlpha(128);
        const int x = 6, y = 8, w = 60, h = 50, r = 14;
        Paint.FillRoundedRect(canvas.AsSurface(), x, y, w, h, r, color);
        uint centre = Px(canvas, x + w / 2, y + h / 2);
        Assert.NotEqual(Bg.Value, centre);
        Assert.NotEqual(Fg.Value, centre);
        for (int py = y; py < y + h; py++)
            for (int px = x; px < x + w; px++)
            {
                int i = Math.Min(px - x, x + w - 1 - px), j = Math.Min(py - y, y + h - 1 - py);
                bool inCorner = i < r && j < r;
                // Every pixel the arcs fully cover is blended exactly once: the same value as the middle.
                if (!inCorner || Paint.CornerCoverage(r, i, j) >= 1f) Assert.Equal(centre, Px(canvas, px, py));
            }
    }

    [Fact]
    public void Rounded_rect_area_matches_geometry()
    {
        using var canvas = new PixelBuffer(120, 120);   // transparent: alpha is the coverage
        const int w = 100, h = 60, r = 20;
        Paint.FillRoundedRect(canvas.AsSurface(), 10, 10, w, h, r, Fg);
        double area = 0;
        for (int py = 0; py < 120; py++)
            for (int px = 0; px < 120; px++) area += Alpha(Px(canvas, px, py)) / 255.0;
        double expected = w * h - (4 - Math.PI) * r * r;
        Assert.InRange(area, expected * 0.99, expected * 1.01);
    }

    [Fact]
    public void Stroke_covers_the_rim_and_leaves_the_inside_alone()
    {
        using var canvas = Canvas(64, 64);
        Paint.StrokeRoundedRect(canvas.AsSurface(), 8, 8, 48, 40, 12, 3, Fg);
        Assert.Equal(Fg.Value, Px(canvas, 30, 8));      // top rim
        Assert.Equal(Fg.Value, Px(canvas, 30, 10));     // 3 px thick: rows 8, 9, 10
        Assert.Equal(Bg.Value, Px(canvas, 30, 11));     // inside
        Assert.Equal(Fg.Value, Px(canvas, 8, 28));      // left rim
        Assert.Equal(Bg.Value, Px(canvas, 31, 28));     // centre untouched
        Assert.Equal(Bg.Value, Px(canvas, 8, 8));       // outside the arc
        // Along the diagonal the rim passes through antialiased pixels.
        int mixed = 0;
        for (int i = 0; i < 12; i++)
        {
            float c = Coverage(Px(canvas, 8 + i, 8 + i), Bg, Fg);
            if (c > 0.05f && c < 0.95f) mixed++;
        }
        Assert.True(mixed >= 1);
    }

    [Fact]
    public void Translucent_stroke_blends_every_pixel_once()
    {
        using var canvas = Canvas(80, 80);
        var color = Fg.WithAlpha(100);
        Paint.StrokeRoundedRect(canvas.AsSurface(), 6, 6, 60, 50, 14, 3, color);
        uint rim = Px(canvas, 30, 6);
        Assert.NotEqual(Bg.Value, rim);
        Assert.Equal(rim, Px(canvas, 30, 7));
        Assert.Equal(rim, Px(canvas, 30, 8));
        Assert.Equal(rim, Px(canvas, 6, 30));
        Assert.Equal(rim, Px(canvas, 65, 30));
        Assert.Equal(rim, Px(canvas, 30, 55));
        // The straight rim and the fully covered part of the arcs are one pixel deep in the same shade.
        for (int i = 0; i < 14; i++)
            for (int j = 0; j < 14; j++)
            {
                float outer = Paint.CornerCoverage(14, i, j), inner = Paint.CornerCoverage(11, i - 3, j - 3);
                if (i >= 3 && j >= 3 && outer >= 1f && inner <= 0f) Assert.Equal(rim, Px(canvas, 6 + i, 6 + j));
            }
    }

    [Fact]
    public void Sharp_stroke_and_oversized_radius_are_handled()
    {
        using var canvas = Canvas(40, 40);
        Paint.StrokeRoundedRect(canvas.AsSurface(), 4, 4, 30, 30, 0, 2, Fg.WithAlpha(120));
        uint rim = Px(canvas, 15, 4);
        Assert.Equal(rim, Px(canvas, 4, 4));            // corners belong to one bar only: no double blend
        Assert.Equal(rim, Px(canvas, 33, 33));
        Assert.Equal(Bg.Value, Px(canvas, 15, 15));
        // A radius past half the side becomes a circle-ended capsule without throwing.
        Paint.FillRoundedRect(canvas.AsSurface(), 4, 4, 30, 10, 100, Fg);
        Paint.StrokeRoundedRect(canvas.AsSurface(), 4, 20, 30, 10, 100, 3, Fg);
        Paint.Capsule(canvas.AsSurface(), 4, 32, 30, 6, Fg);
    }

    [Fact]
    public void Empty_negative_and_extreme_sizes_do_not_throw_or_draw()
    {
        using var canvas = Canvas(32, 32);
        var s = canvas.AsSurface();
        long before = Hash(canvas);
        Paint.FillRoundedRect(s, 5, 5, 0, 10, 4, Fg);
        Paint.FillRoundedRect(s, 5, 5, 10, 0, 4, Fg);
        Paint.FillRoundedRect(s, 5, 5, -10, -10, 4, Fg);
        Paint.FillRoundedRect(s, 5, 5, 10, 10, -4, Fg.WithAlpha(0));
        Paint.StrokeRoundedRect(s, 5, 5, 0, 10, 4, 2, Fg);
        Paint.StrokeRoundedRect(s, 5, 5, 10, 10, 4, 0, Fg);
        Paint.StrokeRoundedRect(s, 5, 5, 10, 10, 4, -3, Fg);
        Paint.FillRect(s, 5, 5, 0, 0, Fg.WithAlpha(100));
        Paint.Capsule(s, 5, 5, 0, 0, Fg);
        Paint.FillCircle(s, 10, 10, 0, Fg);
        Paint.FillCircle(s, 10, 10, -3, Fg);
        Paint.FillCircle(s, 10, 10, float.NaN, Fg);
        Paint.StrokeCircle(s, 10, 10, 6, 0, Fg);
        Paint.StrokeCircle(s, 10, 10, 0, 2, Fg);
        Paint.Line(s, 1, 1, 20, 20, 0, Fg);
        Paint.Line(s, 1, 1, 20, 20, -2, Fg);
        Assert.Equal(before, Hash(canvas));

        // Degenerate but drawable: a 1 px rect or rule, a thickness wider than the shape.
        Paint.FillRoundedRect(s, 2, 2, 1, 1, 8, Fg);
        Paint.StrokeRoundedRect(s, 4, 4, 1, 20, 8, 5, Fg);
        Paint.StrokeRoundedRect(s, 8, 8, 6, 6, 2, 50, Fg);
        Paint.Line(s, 3, 3, 3, 3, 4, Fg);   // a dot
        Paint.Panel(s, 0, 0, 1, 1);
    }

    [Fact]
    public void Shapes_clip_at_and_beyond_the_surface_edges()
    {
        using var canvas = Canvas(40, 40);
        var s = canvas.AsSurface();
        Paint.FillRoundedRect(s, -10, -10, 30, 30, 10, Fg);          // top-left corner off screen
        Paint.FillRoundedRect(s, 30, 30, 30, 30, 10, Fg.WithAlpha(150));   // bottom-right off screen
        Paint.FillRoundedRect(s, -1000000, -1000000, 2000000, 2000000, 16, Fg.WithAlpha(40));   // everything, translucent
        Paint.StrokeRoundedRect(s, -5, 5, 20, 20, 8, 3, Fg);
        Paint.StrokeRoundedRect(s, 30, -5, 20, 20, 8, 3, Fg.WithAlpha(90));
        Paint.FillCircle(s, -2, 20, 9, Fg);
        Paint.StrokeCircle(s, 42, 20, 9, 3, Fg);
        Paint.Line(s, -20, -20, 60, 60, 3, Fg);
        Paint.FocusRing(s, -2, -2, 44, 44, 12);
        Paint.FillRect(s, int.MaxValue - 5, 0, int.MaxValue, 10, Fg.WithAlpha(90));
        Paint.FillRoundedRect(s, 0, 0, int.MaxValue, 40, 8, Fg.WithAlpha(60));
        Paint.Panel(s, -50, -50, 30, 30);

        // What is on screen is still right: the first call fills the visible quarter-disc solidly.
        using var check = Canvas(40, 40);
        Paint.FillRoundedRect(check.AsSurface(), -10, -10, 30, 30, 10, Fg);
        Assert.Equal(Fg.Value, Px(check, 0, 0));
        Assert.Equal(Fg.Value, Px(check, 14, 14));
        Assert.Equal(Bg.Value, Px(check, 19, 19));   // the rectangle's own rounded corner
        Assert.Equal(Bg.Value, Px(check, 20, 20));
    }

    [Fact]
    public void Painting_on_a_transparent_sprite_keeps_the_colour_and_edge_alpha()
    {
        using var sprite = new PixelBuffer(40, 40);
        Paint.FillRoundedRect(sprite.AsSurface(), 0, 0, 40, 40, 14, Fg);
        uint solid = Px(sprite, 20, 20);
        Assert.Equal(Fg.Value, solid);
        int soft = 0;
        for (int j = 0; j < 14; j++)
            for (int i = 0; i < 14; i++)
            {
                uint p = Px(sprite, i, j);
                if (Alpha(p) is > 0 and < 255) { soft++; Assert.Equal((int)Fg.R, Red(p)); }   // colour is not darkened by the empty pixel under it
            }
        Assert.True(soft >= 14);
        Assert.Equal(0u, Px(sprite, 0, 0));
    }

    // ---- Paint: circles, lines, focus ring, panel ----------------------------------------------------------------------

    [Fact]
    public void Circle_area_matches_geometry_and_edges_are_soft()
    {
        using var sprite = new PixelBuffer(64, 64);
        Paint.FillCircle(sprite.AsSurface(), 32f, 32f, 14.5f, Fg);
        double area = 0; int soft = 0;
        for (int py = 0; py < 64; py++)
            for (int px = 0; px < 64; px++)
            {
                int a = Alpha(Px(sprite, px, py));
                area += a / 255.0;
                if (a is > 8 and < 247) soft++;
            }
        Assert.InRange(area, Math.PI * 14.5 * 14.5 * 0.99, Math.PI * 14.5 * 14.5 * 1.01);
        Assert.InRange(soft, 40, 120);
        Assert.Equal(255, Alpha(Px(sprite, 32, 32)));
        Assert.Equal(0, Alpha(Px(sprite, 2, 2)));
    }

    [Fact]
    public void Ring_leaves_its_centre_and_covers_the_expected_area()
    {
        using var sprite = new PixelBuffer(64, 64);
        Paint.StrokeCircle(sprite.AsSurface(), 32f, 32f, 20f, 4f, Fg);
        Assert.Equal(0, Alpha(Px(sprite, 32, 32)));
        Assert.Equal(255, Alpha(Px(sprite, 32 + 17, 32)));   // between radius 16 and 20
        Assert.Equal(0, Alpha(Px(sprite, 32 + 22, 32)));
        double area = 0;
        for (int py = 0; py < 64; py++)
            for (int px = 0; px < 64; px++) area += Alpha(Px(sprite, px, py)) / 255.0;
        double expected = Math.PI * (20 * 20 - 16 * 16);
        Assert.InRange(area, expected * 0.98, expected * 1.02);
    }

    [Fact]
    public void Line_has_round_caps_and_the_requested_thickness()
    {
        using var sprite = new PixelBuffer(48, 24);
        Paint.Line(sprite.AsSurface(), 8f, 12f, 40f, 12f, 4f, Fg);   // covers rows 10 to 13 exactly
        Assert.Equal(255, Alpha(Px(sprite, 24, 10)));
        Assert.Equal(255, Alpha(Px(sprite, 24, 13)));
        Assert.Equal(0, Alpha(Px(sprite, 24, 9)));
        Assert.Equal(0, Alpha(Px(sprite, 24, 14)));
        Assert.InRange(Alpha(Px(sprite, 6, 12)), 1, 254);   // the cap reaches 2 px past the end point, softly at its edge
        Assert.Equal(0, Alpha(Px(sprite, 4, 12)));
        double area = 0;
        for (int py = 0; py < 24; py++)
            for (int px = 0; px < 48; px++) area += Alpha(Px(sprite, px, py)) / 255.0;
        double expected = 32 * 4 + Math.PI * 4;   // body plus two half discs
        Assert.InRange(area, expected * 0.97, expected * 1.03);
    }

    [Fact]
    public void Focus_ring_sits_six_pixels_out_with_a_glow_beyond_it()
    {
        using var canvas = Canvas(120, 100);
        const int x = 30, y = 30, w = 60, h = 40, r = 16;
        Paint.FocusRing(canvas.AsSurface(), x, y, w, h, r);
        int midY = y + h / 2;
        Assert.Equal(Theme.White.Value, Px(canvas, x - 6, midY));       // ring: outer edge 6 out, 3 px wide
        Assert.Equal(Theme.White.Value, Px(canvas, x - 4, midY));
        Assert.Equal(Bg.Value, Px(canvas, x - 3, midY));                // a gap between ring and element
        Assert.Equal(Bg.Value, Px(canvas, x - 1, midY));
        Assert.Equal(Bg.Value, Px(canvas, x + 5, midY));                // never touches the element
        // The glow: fainter with every step, then gone.
        float g1 = Coverage(Px(canvas, x - 7, midY), Bg, Theme.Accent), g2 = Coverage(Px(canvas, x - 9, midY), Bg, Theme.Accent),
              g3 = Coverage(Px(canvas, x - 11, midY), Bg, Theme.Accent);
        Assert.True(g1 > g2 && g2 > g3 && g3 > 0f, $"{g1} {g2} {g3}");
        Assert.Equal(Bg.Value, Px(canvas, x - 13, midY));
        // Concentric corners: the ring's corner is rounder, not square.
        Assert.Equal(Bg.Value, Px(canvas, x - 6, y - 6));
        Paint.FocusRing(canvas.AsSurface(), 4, 4, 20, 20, 4, Theme.Accent);   // custom colour, tight radius
    }

    [Fact]
    public void Panel_is_a_translucent_glass_with_a_faint_edge()
    {
        using var canvas = Canvas(100, 80, Color.FromRgb(200, 60, 60));
        Paint.Panel(canvas.AsSurface(), 10, 10, 80, 60);
        uint inside = Px(canvas, 50, 40);
        Assert.True(Red(inside) < 60, "the panel darkens a bright background");
        Assert.True(Red(inside) > Theme.Ink.R, "but it is translucent, not solid ink");
        Assert.Equal(255, Alpha(inside));
        Assert.Equal(Color.FromRgb(200, 60, 60).Value, Px(canvas, 5, 5));
        Assert.NotEqual(inside, Px(canvas, 50, 10));    // the 1 px edge differs from the fill
        Assert.Equal(Color.FromRgb(200, 60, 60).Value, Px(canvas, 10, 10));   // rounded corner: the very corner stays clear
    }

    [Fact]
    public void Fast_translucent_fill_matches_the_sdk_blend_on_the_display()
    {
        using var a = Canvas(32, 16, Color.FromRgb(90, 140, 200));
        using var b = Canvas(32, 16, Color.FromRgb(90, 140, 200));
        var color = Color.FromArgb(185, 7, 12, 21);
        Paint.FillRect(a.AsSurface(), 2, 3, 20, 9, color);
        b.AsSurface().FillRectBlended(2, 3, 20, 9, color);
        for (int py = 0; py < 16; py++)
            for (int px = 0; px < 32; px++) Assert.Equal(Px(b, px, py), Px(a, px, py));
    }

    // ---- ImageCache ---------------------------------------------------------------------------------------------------

    private static PixelBuffer Gradient(int w, int h)
    {
        var b = new PixelBuffer(w, h);
        b.AsSurface().FillVerticalGradient(0, 0, w, h, Color.FromRgb(200, 80, 40), Color.FromRgb(30, 60, 220));
        return b;
    }

    private static long Hash(PixelBuffer b)
    {
        long h = 17;
        uint* pixels = b.AsSurface().Pixels;
        for (int i = 0; i < b.Width * b.Height; i++) h = unchecked(h * 31 + pixels[i]);
        return h;
    }

    [Fact]
    public void Cached_image_draws_opaque_inside_and_rounded_at_the_corners()
    {
        using var cache = new ImageCache();
        using var source = Gradient(200, 120);
        var image = cache.Add("a", source, 80, 48, 12);
        using var canvas = Canvas(100, 70);
        image.Draw(canvas.AsSurface(), 10, 8);
        Assert.Equal(Bg.Value, Px(canvas, 10, 8));            // corner cut away
        Assert.Equal(Bg.Value, Px(canvas, 89, 55));
        Assert.Equal(Bg.Value, Px(canvas, 9, 30));            // nothing outside
        Assert.Equal(Bg.Value, Px(canvas, 90, 30));
        Assert.Equal(Bg.Value, Px(canvas, 40, 7));
        Assert.Equal(Bg.Value, Px(canvas, 40, 56));
        for (int py = 8; py < 56; py++)
            for (int px = 10; px < 90; px++)
            {
                int i = Math.Min(px - 10, 89 - px), j = Math.Min(py - 8, 55 - py);
                bool corner = i < 12 && j < 12;
                if (!corner || Paint.CornerCoverage(12, i, j) >= 1f) Assert.Equal(255, Alpha(Px(canvas, px, py)));
            }
        // Partially covered corner pixels are between the image and the background.
        int soft = 0;
        for (int i = 0; i < 12; i++)
        {
            uint p = Px(canvas, 10 + i, 8 + i);
            if (p != Bg.Value && Red(p) != Red(Px(canvas, 10 + 20, 8 + i))) soft++;
        }
        Assert.True(soft >= 1);
        // Image content is where it should be: the top of the gradient is the warm end.
        Assert.True(Red(Px(canvas, 50, 10)) > Red(Px(canvas, 50, 53)));
    }

    [Fact]
    public void Cached_image_corners_match_a_painted_rounded_rect_of_the_same_radius()
    {
        using var cache = new ImageCache();
        using var white = new PixelBuffer(60, 40);
        white.Clear(Color.White);
        using var viaImage = Canvas(60, 40);
        using var viaPaint = Canvas(60, 40);
        cache.Add("w", white, 60, 40, 14).Draw(viaImage.AsSurface(), 0, 0);
        Paint.FillRoundedRect(viaPaint.AsSurface(), 0, 0, 60, 40, 14, Color.White);
        for (int py = 0; py < 40; py++)
            for (int px = 0; px < 60; px++) Assert.InRange(Math.Abs(Red(Px(viaImage, px, py)) - Red(Px(viaPaint, px, py))), 0, 2);
    }

    [Fact]
    public void Cache_hits_return_the_same_image_and_every_variant_has_its_own_entry()
    {
        using var cache = new ImageCache();
        using var source = Gradient(64, 64);
        Assert.Null(cache.TryGet("k", 32, 32, 8));
        var a = cache.Add("k", source, 32, 32, 8);
        Assert.Same(a, cache.TryGet("k", 32, 32, 8));
        Assert.Null(cache.TryGet("k", 32, 32, 8, dim: 75));
        Assert.Null(cache.TryGet("k", 32, 33, 8));
        Assert.Null(cache.TryGet("k", 32, 32, 9));
        Assert.Null(cache.TryGet("other", 32, 32, 8));
        var dimmed = cache.Add("k", source, 32, 32, 8, dim: 75);
        Assert.NotSame(a, dimmed);
        Assert.Equal(2, cache.Count);
        Assert.Equal(2L * 32 * 32 * 4, cache.Bytes);
        // The dimmed copy is darker, toward Ink, corners and all.
        using var c1 = Canvas(32, 32); using var c2 = Canvas(32, 32);
        a.Draw(c1.AsSurface(), 0, 0); dimmed.Draw(c2.AsSurface(), 0, 0);
        Assert.True(Red(Px(c2, 16, 5)) < Red(Px(c1, 16, 5)));
        Assert.Equal(Bg.Value, Px(c2, 0, 0));
        // Re-adding the same key replaces it rather than growing the cache.
        var again = cache.Add("k", source, 32, 32, 8);
        Assert.Equal(2, cache.Count);
        Assert.Same(again, cache.TryGet("k", 32, 32, 8));
        Assert.Throws<ObjectDisposedException>(() => a.Draw(c1.AsSurface(), 0, 0));   // the replaced image was released
    }

    [Fact]
    public void Cache_is_bounded_and_drops_the_least_recently_used()
    {
        using var cache = new ImageCache(maxEntries: 3);
        using var source = Gradient(16, 16);
        for (int i = 0; i < 3; i++) cache.Add("e" + i, source, 8, 8);
        Assert.NotNull(cache.TryGet("e0", 8, 8));       // touched: now e1 is the oldest
        cache.Add("e3", source, 8, 8);
        Assert.Equal(3, cache.Count);
        Assert.Null(cache.TryGet("e1", 8, 8));
        Assert.NotNull(cache.TryGet("e0", 8, 8));
        Assert.NotNull(cache.TryGet("e2", 8, 8));
        Assert.NotNull(cache.TryGet("e3", 8, 8));
        for (int i = 10; i < 40; i++) cache.Add("e" + i, source, 8, 8);
        Assert.Equal(3, cache.Count);

        using var budget = new ImageCache(maxEntries: 100, maxBytes: 3 * 8 * 8 * 4);
        for (int i = 0; i < 10; i++) budget.Add("b" + i, source, 8, 8);
        Assert.Equal(3, budget.Count);
        Assert.True(budget.Bytes <= 3 * 8 * 8 * 4);
        var big = budget.Add("big", source, 16, 16);    // bigger than the whole budget: kept, alone
        Assert.Equal(1, budget.Count);
        big.Draw(Canvas(16, 16).AsSurface(), 0, 0);
    }

    [Fact]
    public void Cache_keeps_copies_not_the_source()
    {
        using var cache = new ImageCache();
        var source = Gradient(64, 64);
        var image = cache.Add("s", source, 32, 32, 6);
        long hash;
        using (var canvas = Canvas(32, 32)) { image.Draw(canvas.AsSurface(), 0, 0); hash = Hash(canvas); }
        source.Dispose();                                // the renderer disposes covers it evicts
        using var after = Canvas(32, 32);
        image.Draw(after.AsSurface(), 0, 0);
        Assert.Equal(hash, Hash(after));
        Assert.NotNull(cache.TryGet("s", 32, 32, 6));
    }

    [Fact]
    public void Remove_clear_and_dispose_release_images()
    {
        var cache = new ImageCache();
        using var source = Gradient(16, 16);
        var keep = cache.Add("keep", source, 8, 8);
        var gone1 = cache.Add("gone", source, 8, 8);
        var gone2 = cache.Add("gone", source, 12, 12, 2);
        cache.Remove("gone");
        Assert.Equal(1, cache.Count);
        Assert.Equal(8L * 8 * 4, cache.Bytes);
        Assert.Null(cache.TryGet("gone", 8, 8));
        using var canvas = Canvas(16, 16);
        Assert.Throws<ObjectDisposedException>(() => gone1.Draw(canvas.AsSurface(), 0, 0));
        Assert.Throws<ObjectDisposedException>(() => gone2.Draw(canvas.AsSurface(), 0, 0));
        keep.Draw(canvas.AsSurface(), 0, 0);
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0L, cache.Bytes);
        Assert.Throws<ObjectDisposedException>(() => keep.Draw(canvas.AsSurface(), 0, 0));
        cache.Add("x", source, 8, 8);
        cache.Dispose();
        cache.Dispose();   // twice is fine
    }

    [Fact]
    public void Scaling_down_averages_instead_of_dropping_pixels()
    {
        using var cache = new ImageCache();
        using var stripes = new PixelBuffer(120, 120);
        for (int y = 0; y < 120; y++)
            stripes.AsSurface().FillRect(0, y, 120, 1, y % 2 == 0 ? Color.FromRgb(0, 0, 0) : Color.FromRgb(255, 255, 255));
        var thumb = cache.Add("stripes", stripes, 25, 25);   // nearest-neighbour would give pure black or pure white rows
        using var canvas = Canvas(25, 25);
        thumb.Draw(canvas.AsSurface(), 0, 0);
        for (int y = 2; y < 23; y++) Assert.InRange(Red(Px(canvas, 12, y)), 100, 156);
    }

    [Fact]
    public void Same_size_source_is_copied_and_transparency_is_flattened_over_ink()
    {
        using var cache = new ImageCache();
        using var source = new PixelBuffer(16, 16);           // fully transparent, with one opaque and one half pixel
        source.AsSurface().SetPixel(3, 3, Color.FromRgb(200, 100, 50));
        source.AsSurface().SetPixel(4, 3, Color.FromArgb(128, 200, 100, 50));
        var image = cache.Add("t", source, 16, 16);
        using var canvas = Canvas(16, 16, Color.FromRgb(255, 0, 255));
        image.Draw(canvas.AsSurface(), 0, 0);
        Assert.Equal(Color.FromRgb(200, 100, 50).Value, Px(canvas, 3, 3));
        Assert.Equal(Theme.Ink.Value, Px(canvas, 8, 8));       // transparent became ink, not the magenta behind
        int half = Red(Px(canvas, 4, 3));
        Assert.InRange(half, 90, 140);
        Assert.Equal(255, Alpha(Px(canvas, 8, 8)));
    }

    [Fact]
    public void Backdrop_is_blurred_dimmed_full_screen_and_cached_by_name()
    {
        using var cache = new ImageCache();
        using var source = new PixelBuffer(960, 540);
        for (int y = 0; y < 540; y += 6)
            for (int x = 0; x < 960; x += 6)
                source.AsSurface().FillRect(x, y, 6, 6, ((x + y) / 6) % 2 == 0 ? Color.White : Color.FromRgb(0, 0, 0));
        var backdrop = cache.GetBackdrop("bd", source, blurRadius: 5, dim: 128);
        Assert.Equal(1920, backdrop.Width);
        Assert.Equal(1080, backdrop.Height);
        Assert.Same(backdrop, cache.GetBackdrop("bd", source, blurRadius: 5, dim: 128));
        // A checkerboard of black and white blurs to grey, and the Ink wash pulls it darker still.
        for (int y = 300; y < 310; y++)
            for (int x = 900; x < 910; x++)
            {
                int red = Red(Px(backdrop, x, y));
                Assert.InRange(red, 40, 110);
                Assert.Equal(255, Alpha(Px(backdrop, x, y)));
            }
        // Old entries are dropped when the cap is reached; the newest stays.
        cache.GetBackdrop("bd2", source);
        cache.GetBackdrop("bd3", source);
        Assert.NotNull(cache.GetBackdrop("bd3", source));
        cache.Remove("bd3");
        cache.Clear();
    }

    // ---- ButtonGlyph ------------------------------------------------------------------------------------------------

    [Fact]
    public void Glyph_widths_and_heights_follow_the_established_proportions()
    {
        Assert.Equal(30, ButtonGlyph.Width(PadButton.Cross, 30));
        Assert.Equal(30, ButtonGlyph.Width(PadButton.DPad, 30));
        Assert.Equal(48, ButtonGlyph.Width(PadButton.L1, 30));
        Assert.Equal(48, ButtonGlyph.Width(PadButton.R2, 30));
        Assert.Equal(45, ButtonGlyph.Width(PadButton.Options, 30));
    }

    [Fact]
    public void Every_glyph_draws_inside_its_box_with_antialiased_edges()
    {
        var font = Font("inter-22.p5font");
        foreach (PadButton button in Enum.GetValues<PadButton>())
        {
            using var canvas = Canvas(90, 60);
            int returned = ButtonGlyph.Draw(canvas.AsSurface(), button, 20, 15, 30, Theme.Ink, Theme.Muted, font);
            Assert.Equal(ButtonGlyph.Width(button, 30), returned);
            bool anyFg = false; int soft = 0;
            for (int py = 0; py < 60; py++)
                for (int px = 0; px < 90; px++)
                {
                    uint p = Px(canvas, px, py);
                    bool inside = px >= 20 && px < 20 + returned && py >= 15 && py < 45;
                    if (!inside) { Assert.Equal(Bg.Value, p); continue; }
                    if (p != Bg.Value && Red(p) != Theme.Muted.R && Red(p) != Theme.Ink.R) soft++;
                    if (p == Theme.Ink.Value || p == Theme.Muted.Value) anyFg = true;
                }
            Assert.True(anyFg, button.ToString());
            Assert.True(soft >= 8, $"{button}: expected antialiased edges, got {soft} soft pixels");
        }
    }

    [Fact]
    public void Round_glyphs_are_a_disc_with_a_symbol_and_the_same_every_time()
    {
        foreach (var button in new[] { PadButton.Cross, PadButton.Circle, PadButton.Square, PadButton.Triangle })
        {
            using var first = Canvas(40, 40); using var second = Canvas(40, 40);
            ButtonGlyph.Draw(first.AsSurface(), button, 5, 5, 30, Theme.Ink, Theme.Muted);
            ButtonGlyph.Draw(second.AsSurface(), button, 5, 5, 30, Theme.Ink, Theme.Muted);
            Assert.Equal(Hash(first), Hash(second));
            Assert.Equal(Bg.Value, Px(first, 5, 5));                  // the disc's corner is empty
            Assert.Equal(Theme.Muted.Value, Px(first, 8, 20));        // the disc itself, away from the symbol
            int symbol = 0;   // pixels near the centre that are not the disc's colour: the symbol
            for (int py = 12; py < 29; py++)
                for (int px = 12; px < 29; px++) if (Px(first, px, py) != Theme.Muted.Value) symbol++;
            Assert.InRange(symbol, 20, 250);
        }
    }

    [Fact]
    public void Glyph_sprites_survive_clearing_and_concurrent_drawing()
    {
        var font = Font("inter-22.p5font");
        using var reference = Canvas(60, 40);
        ButtonGlyph.Draw(reference.AsSurface(), PadButton.R1, 3, 3, 37, Theme.Ink, Theme.Muted, font);
        long expected = Hash(reference);
        ButtonGlyph.Clear();
        using var again = Canvas(60, 40);
        ButtonGlyph.Draw(again.AsSurface(), PadButton.R1, 3, 3, 37, Theme.Ink, Theme.Muted, font);
        Assert.Equal(expected, Hash(again));

        var buttons = Enum.GetValues<PadButton>();
        Parallel.For(0, 8, worker =>
        {
            using var canvas = Canvas(90, 50);
            for (int i = 0; i < 200; i++)
            {
                var button = buttons[(worker + i) % buttons.Length];
                ButtonGlyph.Draw(canvas.AsSurface(), button, 4, 4, 24 + (i % 7), Theme.Ink, Theme.Muted, font);
                if (worker == 0 && i % 50 == 0) ButtonGlyph.Clear();
            }
        });
        Assert.True(ButtonGlyph.CachedCount >= 0);
        ButtonGlyph.Draw(Canvas(10, 10).AsSurface(), PadButton.Cross, 0, 0, 0, Theme.Ink, Theme.Muted);   // size 0: nothing, no throw
    }

    // ---- AtlasFont.Wrap / DrawCentered -------------------------------------------------------------------------------

    [Fact]
    public void Wrap_keeps_text_that_fits_on_one_line()
    {
        var font = Font();
        const string text = "A still ocean. A different world.";
        Assert.Equal(new[] { text }, font.Wrap(text, font.MeasureText(text), 3));
        Assert.Equal(new[] { text }, font.Wrap(text, 5000, 1));
    }

    [Fact]
    public void Wrap_breaks_at_word_boundaries()
    {
        var font = Font();
        int width = font.MeasureText("Hello world") - 1;
        Assert.Equal(new[] { "Hello", "world" }, font.Wrap("Hello world", width, 5));
        string text = "The quick brown fox jumps over the lazy dog and keeps running far away";
        var lines = font.Wrap(text, 330, 20);
        Assert.True(lines.Count >= 3);
        foreach (string line in lines) Assert.True(font.MeasureText(line) <= 330, line);
        Assert.Equal(text, string.Join(" ", lines));
        // Greedy: the first word of the next line would not have fitted on the line before it.
        for (int i = 0; i + 1 < lines.Count; i++)
            Assert.True(font.MeasureText(lines[i] + " " + lines[i + 1].Split(' ')[0]) > 330);
    }

    [Fact]
    public void Wrap_ends_truncated_text_with_an_ellipsis_that_still_fits()
    {
        var font = Font();
        string text = "The quick brown fox jumps over the lazy dog and keeps running far away from here";
        var all = font.Wrap(text, 330, 20);
        var cut = font.Wrap(text, 330, 2);
        Assert.Equal(2, cut.Count);
        Assert.Equal(all[0], cut[0]);
        Assert.EndsWith("…", cut[1]);
        Assert.True(font.MeasureText(cut[1]) <= 330);
        Assert.StartsWith(all[1].Split(' ')[0], cut[1]);
        Assert.DoesNotContain(" …", cut[1]);
        // Exactly as many lines as allowed is not truncation.
        var exact = font.Wrap(text, 330, all.Count);
        Assert.Equal(all, exact);
        Assert.DoesNotContain(exact, l => l.EndsWith("…"));
        // One line only.
        var one = font.Wrap(text, 330, 1);
        Assert.Single(one);
        Assert.EndsWith("…", one[0]);
        Assert.True(font.MeasureText(one[0]) <= 330);
    }

    [Fact]
    public void Wrap_breaks_a_word_longer_than_the_line()
    {
        var font = Font();
        string word = "Supercalifragilisticexpialidocious";
        var lines = font.Wrap(word, 120, 20);
        Assert.True(lines.Count >= 3);
        foreach (string line in lines) Assert.True(font.MeasureText(line) <= 120, line);
        Assert.Equal(word, string.Concat(lines));
        // Words after it continue on the last piece's line when they fit.
        var mixed = font.Wrap("ok " + word + " end", 120, 20);
        Assert.Equal("ok", mixed[0]);
        Assert.EndsWith("end", mixed[^1]);
        Assert.Equal("ok" + word + "end", string.Concat(mixed.Select(l => l.Replace(" ", ""))));
        // A width narrower than any letter still makes progress, one letter a line.
        var narrow = font.Wrap("abc", 1, 10);
        Assert.Equal(new[] { "a", "b", "c" }, narrow);
        // Truncating inside a long word.
        var cut = font.Wrap(word, 120, 2);
        Assert.Equal(2, cut.Count);
        Assert.EndsWith("…", cut[1]);
        Assert.True(font.MeasureText(cut[1]) <= 120);
    }

    [Fact]
    public void Wrap_handles_empty_blank_and_degenerate_input()
    {
        var font = Font();
        Assert.Empty(font.Wrap("", 300, 3));
        Assert.Empty(font.Wrap("   ", 300, 3));
        Assert.Empty(font.Wrap("\n\n", 300, 3));
        Assert.Empty(font.Wrap("text", 0, 3));
        Assert.Empty(font.Wrap("text", -5, 3));
        Assert.Empty(font.Wrap("text", 300, 0));
        Assert.Empty(font.Wrap("text", 300, -1));
        Assert.Equal(new[] { "one", "two" }, font.Wrap("one\ntwo", 300, 5));
        Assert.Equal(new[] { "one", "", "two" }, font.Wrap("one\n\ntwo", 300, 5));
        Assert.Equal(new[] { "one", "two" }, font.Wrap("one\r\ntwo\n", 300, 5));   // a trailing newline does not truncate
        Assert.Equal(new[] { "one", "two" }, font.Wrap("  one   \n  two  ", 300, 2));
        Assert.Equal(new[] { "a b" }, font.Wrap("a    b", 300, 2));
        Assert.Equal(3, font.Wrap(string.Join(" ", Enumerable.Repeat("word", 5000)), 300, 3).Count);   // stops early on long texts
        Assert.NotEmpty(font.Wrap("x", 300, int.MaxValue));
    }

    [Fact]
    public void Draw_centered_puts_the_text_around_the_given_x()
    {
        var font = Font();
        using var canvas = Canvas(400, 60);
        font.DrawCentered(canvas.AsSurface(), "Centered", 200, 10, Color.White);
        int min = int.MaxValue, max = int.MinValue;
        for (int py = 0; py < 60; py++)
            for (int px = 0; px < 400; px++)
                if (Px(canvas, px, py) != Bg.Value) { min = Math.Min(min, px); max = Math.Max(max, px); }
        Assert.InRange((min + max) / 2, 192, 208);
        Assert.InRange(max - min, font.MeasureText("Centered") - 16, font.MeasureText("Centered"));
    }

    // ---- ThemeRenderer: card images ---------------------------------------------------------------------------------

    [Fact]
    public void Community_card_shows_its_preview_once_it_arrives_and_decodes_it_once()
    {
        int decodes = 0;
        using var renderer = new ThemeRenderer(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)), _ =>
        {
            decodes++;
            var b = new PixelBuffer(8, 4);
            b.Clear(Color.FromRgb(250, 40, 40));
            return b;
        });
        var themes = Enumerable.Range(0, 2).Select(i =>
            new CommunityTheme("id" + i, "Theme " + i, "Me", "1.0", "", 1, i, i, null, "http://x", true, false, 0, 0, 0)).ToList();
        renderer.Community = themes;
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        browser.SetCommunityCount(2);
        browser.Update(UiInput.None with { PageDown = true });
        Assert.Equal(ThemeView.Community, browser.View);

        long Frame() { renderer.Draw(canvas.AsSurface(), browser, Array.Empty<ThemeLibraryEntry>(), 5); return Hash(canvas); }
        long placeholder = Frame();
        Assert.Equal(0, decodes);
        Assert.Equal(placeholder, Frame());

        renderer.CommunityPreviews["id1"] = new byte[] { 1 };      // the second card's preview arrives
        long withPreview = Frame();
        Assert.NotEqual(placeholder, withPreview);
        Assert.Equal(1, decodes);
        Assert.Equal(withPreview, Frame());
        Frame(); Frame();
        Assert.Equal(1, decodes);                                  // cached: no decode on later frames

        renderer.ResetCommunity();                                 // a new list: previews and card images start over
        Assert.Equal(placeholder, Frame());
        Assert.Equal(1, decodes);
    }

    [Fact]
    public void Keyboard_screen_uses_a_calmed_backdrop_that_does_not_leak_into_other_screens()
    {
        using var renderer = new ThemeRenderer(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)), _ => new PixelBuffer(4, 4));
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        double now = 5;
        // Two frames per look: the first of a view is mid slide-in.
        long Frame() { renderer.Draw(canvas.AsSurface(), browser, Array.Empty<ThemeLibraryEntry>(), now); now += 3; renderer.Draw(canvas.AsSurface(), browser, Array.Empty<ThemeLibraryEntry>(), now); now += 3; return Hash(canvas); }
        long library = Frame();
        browser.OpenStoreAddress("http://a");
        long keyboard = Frame();
        Assert.Equal(keyboard, Frame());
        // Below the header the cover is washed with ink; the header itself is untouched.
        Assert.True(Red(Px(canvas, 1880, 600)) <= Theme.Ink.R + 12);   // right margin, past the keys
        browser.Update(UiInput.None with { Cancel = true });   // back to the system screen
        browser.OpenStoreAddress("http://a");
        Assert.Equal(keyboard, Frame());
        browser.Update(UiInput.None with { Cancel = true });
        browser.Update(UiInput.None with { PageUp = true });
        browser.Update(UiInput.None with { PageUp = true });
        browser.Update(UiInput.None with { PageUp = true });
        Assert.Equal(ThemeView.Library, browser.View);
        Assert.Equal(library, Frame());
    }

    [Fact]
    public void Library_thumbnails_are_cached_and_follow_the_selection()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "two-screen.p5theme"));
        var entry = ThemeLibraryEntry.Inspect("original", bytes);
        var entries = Enumerable.Range(0, 4).Select(i => entry with { Path = "p" + i, Preview = new byte[] { (byte)i } }).ToArray();
        int decodes = 0;
        using var renderer = new ThemeRenderer(name => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "renderer", name)), data =>
        {
            decodes++;
            var b = new PixelBuffer(32, 18);
            b.Clear(Color.FromRgb((byte)(60 + data[0] * 50), 120, 200));
            return b;
        });
        using var canvas = new PixelBuffer(1920, 1080);
        var browser = new ThemeBrowser();
        browser.SetCount(entries.Length);
        long Frame(double t) { renderer.Draw(canvas.AsSurface(), browser, entries, t); return Hash(canvas); }
        long first = Frame(5);
        browser.Update(new UiInput(false, false, false, true, false, false));
        Frame(6);
        long second = Frame(7);
        Assert.NotEqual(first, second);
        int settled = decodes;
        for (int i = 0; i < 5; i++) Frame(8 + i);
        Assert.Equal(settled, decodes);
        // Opening the detail view draws the big cover; it is cached too.
        browser.Update(new UiInput(false, false, false, false, true, false));
        Frame(20);
        int afterDetail = decodes;
        for (int i = 0; i < 3; i++) Frame(21 + i);
        Assert.Equal(afterDetail, decodes);
    }
}
