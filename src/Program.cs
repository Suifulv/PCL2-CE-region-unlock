using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using ICSharpCode.Decompiler;

internal static class Program
{
    private const string DllName = "Plain Craft Launcher 2.dll";

    // .NET single-file bundle 的 32 字节签名（SHA-256 of ".net core bundle"）
    private static readonly byte[] BundleSignature =
    {
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    };

    // 目标方法 -> (tiny method header + IL, 说明)
    // get_IsRestricted : ldc.i4.0; ret
    // Check            : ldc.i4.1; ret
    // ThrowIfRestricted: ret
    private static readonly Dictionary<string, (byte[] Body, string Desc)> Targets = new()
    {
        ["get_IsRestricted"] = (new byte[] { 0x0A, 0x16, 0x2A }, "ldc.i4.0; ret  (IsRestricted => false)"),
        ["Check"] = (new byte[] { 0x0A, 0x17, 0x2A }, "ldc.i4.1; ret  (Check => true)"),
        ["ThrowIfRestricted"] = (new byte[] { 0x06, 0x2A }, "ret          (ThrowIfRestricted 空方法)")
    };

    private static int Main(string[] args)
    {
        var paths = new List<string>();
        var backup = true;
        var inspect = false;
        var noPause = false;

        foreach (var arg in args)
        {
            if (arg == "--inspect") inspect = true;
            else if (arg == "--no-backup") backup = false;
            else if (arg == "--no-pause") noPause = true;
            else if (arg is "--help" or "-h" or "/?") { PrintHelp(); return 0; }
            else if (arg.StartsWith('-')) { Console.Error.WriteLine("未知选项: " + arg); return 2; }
            else paths.Add(arg);
        }

        if (paths.Count == 0)
        {
            var found = FindDefault();
            if (found == null)
            {
                Console.Error.WriteLine("未找到 PCL2 CE 可执行文件。请把 exe 拖到本程序上，或传入路径。");
                Console.Error.WriteLine("用法: PCL2RegionUnlock.exe \"C:\\Program Files\\PCL2\\PCL2_CE_Release_x64.exe\"");
                return 1;
            }
            paths.Add(found);
        }

        var rc = 0;
        foreach (var path in paths)
            rc |= inspect ? Inspect(path) : Patch(path, backup);

        if (!Console.IsInputRedirected && !noPause)
        {
            Console.WriteLine();
            Console.Write("按任意键退出...");
            Console.ReadKey(true);
        }
        return rc;
    }

    private static int Inspect(string exePath)
    {
        if (!File.Exists(exePath)) { Console.Error.WriteLine("文件不存在: " + exePath); return 1; }
        var exe = File.ReadAllBytes(exePath);
        if (!TryFindBundle(exe, out var headerOffset))
        {
            Console.Error.WriteLine("[X] 不是 .NET single-file bundle: " + exePath);
            return 1;
        }
        var header = ReadManifest(exe, headerOffset);
        Console.WriteLine($"bundle v{header.MajorVersion}.{header.MinorVersion}  {header.FileCount} 个文件  {Path.GetFileName(exePath)}");
        foreach (var e in header.Entries)
            Console.WriteLine($"  {e.RelativePath}  type={e.Type}  off=0x{e.Offset:X}  size={e.Size}" + (e.CompressedSize > 0 ? $"  compressed={e.CompressedSize}" : ""));
        return 0;
    }

    private static int Patch(string exePath, bool backup)
    {
        if (!File.Exists(exePath)) { Console.Error.WriteLine("文件不存在: " + exePath); return 1; }
        Console.WriteLine("== " + exePath);

        var exe = File.ReadAllBytes(exePath);
        if (!TryFindBundle(exe, out var headerOffset))
        {
            Console.Error.WriteLine("[X] 不是 .NET single-file bundle（或签名未找到），无法打补丁。");
            return 1;
        }

        SingleFileBundle.Header header;
        try { header = ReadManifest(exe, headerOffset); }
        catch (Exception ex) { Console.Error.WriteLine("[X] 读取 bundle 清单失败: " + ex.Message); return 1; }

        var entry = header.Entries.FirstOrDefault(e => string.Equals(e.RelativePath, DllName, StringComparison.OrdinalIgnoreCase));
        if (entry.RelativePath == null)
        {
            Console.Error.WriteLine($"[X] bundle 中未找到 {DllName}。");
            return 1;
        }
        if (entry.CompressedSize != 0)
        {
            Console.Error.WriteLine("[X] bundle 内 DLL 已压缩，本工具暂不支持（官方 CI 产物未压缩）。");
            return 1;
        }
        if (entry.Offset < 0 || entry.Size <= 0 || entry.Offset + entry.Size > exe.Length)
        {
            Console.Error.WriteLine("[X] bundle 清单中的 DLL 偏移/大小无效。");
            return 1;
        }

        var dll = exe.AsSpan((int)entry.Offset, (int)entry.Size).ToArray();
        if (!PatchDll(dll, out var report))
        {
            Console.Error.WriteLine(report);
            return 1;
        }
        Console.WriteLine(report);

        // 写回：只覆盖 DLL 区域，保持 exe 长度与 bundle 布局不变
        if (backup)
        {
            var bak = exePath + ".bak";
            if (!File.Exists(bak))
            {
                File.Copy(exePath, bak);
                Console.WriteLine("[备份] " + bak);
            }
            else
            {
                Console.WriteLine("[备份] 已存在，保留首次备份: " + bak);
            }
        }
        Array.Copy(dll, 0, exe, (int)entry.Offset, dll.Length);
        try
        {
            File.WriteAllBytes(exePath, exe);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("[X] 写入失败：文件可能正在被 PCL 运行占用，请先关闭 PCL 再重试。");
            return 1;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine("[X] 写入失败: " + ex.Message + "（请先关闭 PCL）");
            return 1;
        }

        Console.WriteLine("[完成] 已解除区域限制: " + exePath);
        return 0;
    }

    private static bool PatchDll(byte[] dll, out string report)
    {
        var sb = new StringBuilder();
        try
        {
            using var pe = new PEReader(ImmutableArray.Create(dll));
            var md = pe.GetMetadataReader();

            var found = new HashSet<string>();
            var patched = 0;

            foreach (var typeHandle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(typeHandle);
                if (md.GetString(type.Namespace) != "PCL" || md.GetString(type.Name) != "RestrictedFeaturePolicy")
                    continue;

                foreach (var methodHandle in type.GetMethods())
                {
                    var method = md.GetMethodDefinition(methodHandle);
                    var name = md.GetString(method.Name);
                    if (!Targets.TryGetValue(name, out var target))
                        continue;

                    found.Add(name);
                    var rva = method.RelativeVirtualAddress;
                    if (rva == 0)
                    {
                        sb.AppendLine($"[!] {name}: 无方法体（rva=0），跳过");
                        continue;
                    }

                    var fileOffset = RvaToFileOffset(pe.PEHeaders, rva);
                    if (fileOffset < 0 || fileOffset + target.Body.Length > dll.Length)
                    {
                        sb.AppendLine($"[!] {name}: 文件偏移 0x{fileOffset:X} 越界");
                        continue;
                    }

                    var original = dll.AsSpan(fileOffset, Math.Min(8, target.Body.Length)).ToArray();
                    var current = dll.AsSpan(fileOffset, target.Body.Length);
                    if (current.SequenceEqual(target.Body))
                    {
                        sb.AppendLine($"[=] {name}: 已是目标 IL（0x{fileOffset:X}），跳过");
                        continue;
                    }

                    target.Body.CopyTo(dll.AsSpan(fileOffset, target.Body.Length));
                    patched++;
                    sb.AppendLine($"[+] {name}: 0x{fileOffset:X} 原字节 [{BitConverter.ToString(original)}] -> {target.Desc}");
                }
            }

            if (!found.Contains("get_IsRestricted") || !found.Contains("Check") || !found.Contains("ThrowIfRestricted"))
            {
                var missing = Targets.Keys.Where(k => !found.Contains(k));
                report = sb + "[X] 未找到目标方法: " + string.Join(", ", missing) + "。官方实现可能已改动，本工具需要更新。";
                return false;
            }

            report = sb + (patched == 0 ? "[OK] 该文件已打过补丁，无需修改。" : $"[OK] 已修改 {patched} 个方法。");
            return patched > 0;
        }
        catch (BadImageFormatException ex)
        {
            report = "[X] 无法解析 DLL: " + ex.Message;
            return false;
        }
    }

    private static int RvaToFileOffset(PEHeaders headers, int rva)
    {
        foreach (var section in headers.SectionHeaders)
        {
            var size = Math.Max(section.VirtualSize, section.SizeOfRawData);
            if (rva >= section.VirtualAddress && rva < section.VirtualAddress + size)
                return rva - section.VirtualAddress + section.PointerToRawData;
        }
        return -1;
    }

    private static bool TryFindBundle(byte[] data, out long headerOffset)
    {
        for (var i = 0; i <= data.Length - BundleSignature.Length; i++)
        {
            if (!data.AsSpan(i, BundleSignature.Length).SequenceEqual(BundleSignature))
                continue;
            if (i < sizeof(long))
                continue;
            var offset = BitConverter.ToInt64(data, i - sizeof(long));
            if (offset > 0 && offset < data.Length)
            {
                headerOffset = offset;
                return true;
            }
        }
        headerOffset = 0;
        return false;
    }

    private static SingleFileBundle.Header ReadManifest(byte[] data, long headerOffset)
    {
        using var ms = new MemoryStream(data, writable: false);
        ms.Seek(headerOffset, SeekOrigin.Begin);
        return SingleFileBundle.ReadManifest(ms);
    }

    private static string? FindDefault()
    {
        const string exeName = "PCL2_CE_Release_x64.exe";
        var candidates = new List<string>();

        var env = Environment.GetEnvironmentVariable("PCL2_EXE_PATH");
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add(env);

        candidates.Add(Path.Combine(AppContext.BaseDirectory, exeName));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, exeName));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
            candidates.Add(Path.Combine(programFiles, "PCL2", exeName));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            candidates.Add(Path.Combine(localAppData, "PCL2", exeName));

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, "Downloads", exeName));
            candidates.Add(Path.Combine(userProfile, "Desktop", exeName));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("PCL2 CE 区域限制修复工具（PCL2RegionUnlock）— 给 PCL2 CE 官方 exe 原地解除中国大陆区域限制");
        Console.WriteLine();
        Console.WriteLine("用法:");
        Console.WriteLine("  PCL2RegionUnlock.exe [选项] [exe 路径...]");
        Console.WriteLine();
        Console.WriteLine("不传路径时自动查找常见安装位置。可一次传多个 exe。");
        Console.WriteLine();
        Console.WriteLine("选项:");
        Console.WriteLine("  --inspect     只列出 bundle 清单，不修改");
        Console.WriteLine("  --no-backup   不创建 .bak 备份");
        Console.WriteLine("  --no-pause    结束时不等待按键");
        Console.WriteLine("  -h, --help    显示帮助");
        Console.WriteLine();
        Console.WriteLine("说明: 直接修改 single-file bundle 内 Plain Craft Launcher 2.dll 的 IL，");
        Console.WriteLine("      使 RestrictedFeaturePolicy 恒放行；保持文件长度不变，不改 bundle 布局。");
        Console.WriteLine();
        Console.WriteLine("一键用法: 双击 PCL2RegionUnlock.exe，自动查找并修复常见安装位置；");
        Console.WriteLine("          也可以把官方 PCL2_CE_Release_x64.exe 拖到本程序上。");
        Console.WriteLine("          修改前会自动备份为 *.bak（已存在则保留首次备份）。");
    }
}
