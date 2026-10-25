using System;
using System.Collections.Generic;
using System.IO;
using ProsperoThemes.App.Ui;
using ProsperoThemes.Shared;
using SharpProspero.Compression;
using SharpProspero.Graphics;

/// <summary>
/// A theme pack made in code for reviewing the console's interactive preview on the PC: a cover, the icons of six system
/// tiles and eight control-centre buttons, three apps with their own pictures (one of them a real progressive JPEG from the
/// fixtures), an all-apps picture, a music excerpt and the four UI sounds. Its payloads are stand-ins (the preview never
/// writes them), so it validates but would not apply to a console. Nothing is committed as a binary.
/// </summary>
internal static class PreviewDemoPack
{
    public const string Path = "(demo) preview-demo.p5theme";

    /// <summary>The pack's bytes, or null when the fixtures it borrows a JPEG, an MP3 and a VAG from are missing.</summary>
    public static byte[]? Build(string fixturesFolder)
    {
        string creator = System.IO.Path.Combine(fixturesFolder, "creator-previews.p5theme"), sound = System.IO.Path.Combine(fixturesFolder, "web-ui-sound.p5theme");
        if (!File.Exists(creator) || !File.Exists(sound)) return null;
        ThemePack borrowedCreator = ThemePack.OpenFile(creator), borrowedSound = ThemePack.OpenFile(sound);
        byte[] realJpeg = borrowedCreator.Find("previews/apps/PPSA99169.jpg")!, music = borrowedCreator.Find("previews/music.mp3")!;
        byte[] vag = borrowedSound.Find("sounds/psfx_focus_move.vag")!;

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var targets = new List<ThemePackTarget>();
        void Target(string path, string layer, string file, byte[] bytes, string? resource = null)
        {
            files[file] = bytes;
            targets.Add(new ThemePackTarget(path, layer, file, bytes.Length, ThemePack.Hash(bytes), Resource: resource));
        }

        // The cover, and the pictures behind the tiles.
        files["preview.png"] = PngWriter.Encode(Landscape(0, 222, 245, 40, 240).AsSurface());
        files["previews/allapps.jpg"] = Jpeg(Landscape(2, 160, 195, 55, 210));
        files["previews/apps/PPSA99169.jpg"] = realJpeg;
        files["previews/apps/PPSA01650.jpg"] = Jpeg(Landscape(1, 24, 265, 45, 235));
        files["previews/apps/NPXS40071.jpg"] = Jpeg(Landscape(3, 285, 215, 330, 240));
        files["previews/music.mp3"] = music;

        // The system tiles the pack dresses: six of the nine (the other three stay stock, drawn as stand-ins).
        (string Path, string Name, int Hue, int Emblem)[] tiles =
        [
            ("rnps/NPXS40047/appdb/default/icon0.png", "store", 205, 0),
            ("rnps/NPXS40037/appdb/default/icon0.png", "plus", 48, 1),
            ("rnps/NPXS40071/appdb/default/icon0.png", "library", 275, 2),
            ("rnps/NPXS40071/appdb/NPXS40139/icon0.png", "apps", 160, 3),
            ("rnps/NPXS40016/appdb/NPXS40053/icon0.png", "discover", 335, 1),
            ("rnps/NPXS40016/appdb/NPXS40054/icon0.png", "music", 12, 2),
        ];
        foreach (var tile in tiles) Target(tile.Path, "icon", "assets/tiles/" + tile.Name + ".png", PngWriter.EncodeRgba(TileIcon(tile.Hue, tile.Emblem).AsSurface()));

        // The control centre's icons: eight of twelve (Switcher, Game Base, Share Screen and Accessories stay stand-ins).
        var icons = new Dictionary<string, string>();
        string[] slots = ["iconid_home", "iconid_notification", "iconid_music", "iconid_download", "iconid_sound_speaking", "iconid_mic", "iconid_ps_user", "iconid_power"];
        for (int i = 0; i < slots.Length; i++)
        {
            string file = "previews/icons/" + slots[i] + ".png";
            files[file] = PngWriter.EncodeRgba(ControlIcon(i).AsSurface());
            icons[slots[i]] = file;
        }

        // Backgrounds of single apps (stand-in payloads, as in the creator fixture), one of them with no picture.
        byte[] dds = [.. "DDS "u8.ToArray(), .. new byte[60]];
        Target("apps/PPSA99169/pic0.dds", "background", "assets/PPSA99169/pic0.dds", dds);
        Target("appmeta/PPSA01650/pic0.dds", "background", "assets/PPSA01650/pic0.dds", dds);
        Target("apps/PPSA12345/pic0.dds", "background", "assets/PPSA12345/pic0.dds", dds);
        Target("apps/PPSA33333/pic0.dds", "background", "assets/PPSA33333/pic0.dds", dds);
        Target("apps/PPSA44444/pic0.dds", "background", "assets/PPSA44444/pic0.dds", dds);
        foreach (string clip in new[] { "psfx_focus_move", "psfx_enter", "psfx_cancel", "psfx_cannot_move" })
            Target("vsh_asset/Sce.PlayStation.PUI_UI3.rco", ThemePack.VagLayer, "sounds/" + clip + ".vag", vag, clip);

        var manifest = new ThemePackManifest(ThemePackManifest.CurrentFormat, "preview-demo", "Preview demo", "tests", "1.0.0",
            "Made in code for the PC preview: tile icons, pictures, music and sounds.", "keep", targets, Preview: "preview.png")
        {
            Previews = new ThemePackPreviews(
                new Dictionary<string, string>
                {
                    ["PPSA99169"] = "previews/apps/PPSA99169.jpg",
                    ["PPSA01650"] = "previews/apps/PPSA01650.jpg",
                    ["NPXS40071"] = "previews/apps/NPXS40071.jpg",
                },
                "previews/allapps.jpg", "previews/music.mp3", icons),
        };
        var zip = new ZipBuilder().AddText(ThemePack.ManifestName, manifest.Write());
        foreach (var (name, bytes) in files) zip.Add(name, bytes);
        return zip.ToArray();
    }

    private static byte[] Jpeg(PixelBuffer picture)
    {
        using (picture) return DemoJpeg.Encode(picture);
    }

    // 960x540 smooth scenery: a sky gradient of one hue to another, a sun and layered hills. `kind` varies the layout.
    private static PixelBuffer Landscape(int kind, int hueSky, int hueGround, int sunHue, int sunBrightness)
    {
        var picture = new PixelBuffer(960, 540);
        Surface s = picture.AsSurface();
        s.FillVerticalGradient(0, 0, 960, 540, Shade(hueSky, 0.55f, 0.18f), Shade(hueSky + 25, 0.5f, 0.52f));
        float sunX = 180 + kind * 190, sunY = 170 + (kind % 2) * 40;
        Paint.FillCircle(s, sunX, sunY, 110f, Shade(sunHue, 0.4f, 0.3f, 90));
        Paint.FillCircle(s, sunX, sunY, 64f, Shade(sunHue, 0.55f, Math.Min(0.9f, sunBrightness / 255f)));
        for (int layer = 0; layer < 3; layer++)
        {
            int baseline = 330 + layer * 70;
            for (int x = 0; x < 960; x += 2)
            {
                double wave = Math.Sin((x + kind * 90 + layer * 140) / (95.0 + layer * 40)) * (28 - layer * 6) + Math.Sin(x / 37.0 + layer) * 6;
                int top = (int)(baseline - wave);
                s.FillRect(x, top, 2, 540 - top, Shade(hueGround + layer * 14, 0.45f, 0.30f - layer * 0.07f));
            }
        }
        return picture;
    }

    // A 512x512 tile icon: a diagonal gradient of the hue with a white emblem, full bleed like the console's own.
    private static PixelBuffer TileIcon(int hue, int emblem)
    {
        var icon = new PixelBuffer(512, 512);
        Surface s = icon.AsSurface();
        s.FillVerticalGradient(0, 0, 512, 512, Shade(hue, 0.65f, 0.55f), Shade(hue + 30, 0.7f, 0.22f));
        Color white = Color.FromRgb(255, 255, 255).WithAlpha(235);
        switch (emblem)
        {
            case 0:
                Paint.StrokeRoundedRect(s, 146, 150, 220, 230, 40, 22, white);
                Paint.StrokeCircle(s, 256, 230, 52f, 20f, white);
                break;
            case 1:
                Paint.Line(s, 176f, 256f, 336f, 256f, 34f, white);
                Paint.Line(s, 256f, 176f, 256f, 336f, 34f, white);
                break;
            case 2:
                Paint.FillRoundedRect(s, 150, 190, 212, 150, 50, white);
                Paint.FillCircle(s, 256, 264, 30f, Shade(hue, 0.6f, 0.3f));
                break;
            default:
                for (int row = 0; row < 2; row++)
                    for (int column = 0; column < 2; column++)
                        Paint.FillRoundedRect(s, 150 + column * 116, 150 + row * 116, 96, 96, 26, white);
                break;
        }
        return icon;
    }

    // A 128x128 control-centre icon, white on transparent: a bold disc-and-bar mark that differs per slot.
    private static PixelBuffer ControlIcon(int index)
    {
        var icon = new PixelBuffer(128, 128);
        Surface s = icon.AsSurface();
        Color white = Color.FromRgb(255, 255, 255);
        switch (index % 4)
        {
            case 0:
                Paint.StrokeCircle(s, 64f, 64f, 46f, 12f, white);
                Paint.FillCircle(s, 64f, 64f, 14f, white);
                break;
            case 1:
                Paint.FillRoundedRect(s, 24, 36, 80, 56, 18, white);
                Paint.FillRect(s, 52, 92, 24, 14, white);
                break;
            case 2:
                Paint.Line(s, 32f, 96f, 32f, 40f, 14f, white);
                Paint.Line(s, 64f, 96f, 64f, 24f, 14f, white);
                Paint.Line(s, 96f, 96f, 96f, 56f, 14f, white);
                break;
            default:
                Paint.StrokeRoundedRect(s, 22, 22, 84, 84, 26, 12, white);
                Paint.Line(s, 44f, 64f, 84f, 64f, 12f, white);
                break;
        }
        return icon;
    }

    // A colour of the given hue (degrees), saturation and lightness (0 to 1), optionally translucent.
    private static Color Shade(int hue, float saturation, float lightness, byte alpha = 255)
    {
        hue = ((hue % 360) + 360) % 360;
        float c = (1f - MathF.Abs(2f * lightness - 1f)) * saturation, h = hue / 60f;
        float x = c * (1f - MathF.Abs(h % 2f - 1f)), m = lightness - c / 2f;
        (float r, float g, float b) = (int)h switch
        {
            0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x), 3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x),
        };
        return Color.FromRgb((byte)((r + m) * 255f), (byte)((g + m) * 255f), (byte)((b + m) * 255f)).WithAlpha(alpha);
    }
}
