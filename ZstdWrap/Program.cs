using ZstdWrap;

// PC-side helper for the Zstandard frames the PS5 stores its system wallpapers in.
//
//   info  <frame>                     read a frame's header and block list
//   wrap  <input> <output>            wrap a file in a Raw-block Zstandard frame
//   verify <content> <frame>          decode a Raw-block frame and compare it to a content file
//   selftest <input>                  encode then decode, and confirm the bytes survive

if (args.Length == 0)
{
    Console.Error.WriteLine("""
        usage:
          ZstdWrap info     <frame>
          ZstdWrap wrap     <input> <output>
          ZstdWrap verify   <contentFile> <frameFile>
          ZstdWrap selftest <input>
        """);
    return 2;
}

try
{
    switch (args[0])
    {
        case "info":
        {
            if (args.Length < 2) { Console.Error.WriteLine("info needs a frame path"); return 2; }
            byte[] frame = File.ReadAllBytes(args[1]);
            ZstdFrameInfo info = ZstdRawFrame.Parse(frame);
            Console.WriteLine(info.Describe());
            Console.WriteLine();
            string[] names = { "Raw", "RLE", "Compressed", "Reserved" };
            for (int i = 0; i < info.Blocks.Count; i++)
            {
                (int type, int size, int offset) = info.Blocks[i];
                Console.WriteLine($"  block {i,3}: {names[type],-10} size={size,9} data@{offset}");
            }
            return 0;
        }

        case "wrap":
        {
            if (args.Length < 3) { Console.Error.WriteLine("wrap needs <input> <output>"); return 2; }
            byte[] content = File.ReadAllBytes(args[1]);
            byte[] frame = ZstdRawFrame.EncodeRaw(content);
            File.WriteAllBytes(args[2], frame);
            Console.WriteLine($"wrapped {content.Length} bytes of content into a {frame.Length}-byte Raw-block frame -> {args[2]}");
            return 0;
        }

        case "verify":
        {
            if (args.Length < 3) { Console.Error.WriteLine("verify needs <contentFile> <frameFile>"); return 2; }
            byte[] expected = File.ReadAllBytes(args[1]);
            byte[] frame = File.ReadAllBytes(args[2]);
            byte[] actual = ZstdRawFrame.DecodeRaw(frame);
            if (!expected.AsSpan().SequenceEqual(actual))
            {
                Console.Error.WriteLine($"MISMATCH: content is {expected.Length} bytes, frame decodes to {actual.Length}");
                return 1;
            }
            Console.WriteLine($"MATCH: frame decodes back to exactly the {expected.Length} bytes of {args[1]}");
            return 0;
        }

        case "selftest":
        {
            if (args.Length < 2) { Console.Error.WriteLine("selftest needs <input>"); return 2; }
            byte[] content = File.ReadAllBytes(args[1]);
            byte[] frame = ZstdRawFrame.EncodeRaw(content);
            byte[] back = ZstdRawFrame.DecodeRaw(frame);
            ZstdFrameInfo info = ZstdRawFrame.Parse(frame);
            Console.WriteLine($"content {content.Length} bytes -> frame {frame.Length} bytes in {info.Blocks.Count} Raw block(s)");
            Console.WriteLine($"header: {info.Describe().Replace(Environment.NewLine, " | ")}");
            if (!content.AsSpan().SequenceEqual(back))
            {
                Console.Error.WriteLine("SELFTEST FAILED: decoded bytes differ from the input");
                return 1;
            }
            Console.WriteLine("SELFTEST PASSED: decoded bytes are identical to the input");
            return 0;
        }

        default:
            Console.Error.WriteLine($"unknown command '{args[0]}'");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}
