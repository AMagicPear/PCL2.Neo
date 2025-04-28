using B2R2;
using B2R2.FrontEnd.BinFile;
using B2R2.FrontEnd.BinFile.Mach;
using PCL2.Neo.Utils;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Architecture = System.Runtime.InteropServices.Architecture;

namespace PCL2.Neo.Models.Minecraft.Java;

/// <summary>
/// 描述某个 Java 环境与当前系统是否兼容
/// </summary>
public enum JavaCompability
{
    /// <summary>
    /// 未知，例如尚未初始化
    /// </summary>
    Unknown,

    /// <summary>
    /// 能够原生运行
    /// </summary>
    Yes,

    /// <summary>
    /// 不兼容
    /// </summary>
    No,

    /// <summary>
    /// 对于 Win11 on Arm 或者 M 芯片 macOS 可以转译运行
    /// </summary>
    UnderTranslation,

    /// <summary>
    /// 初始化失败或者不是 Java 可执行文件
    /// </summary>
    Error,
}

/// <summary>
/// 每一个 Java 实体的信息类
/// </summary>
public class JavaRuntime
{
    /// <summary>
    /// 该 Java 实体的父目录，在构造时传入
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// 描述具体的 Java 信息，内部信息，不应在外部取用
    /// </summary>
    private JavaInfo _javaInfo;

    /// <summary>
    /// 私有构造函数，需要路径和信息
    /// </summary>
    /// <param name="directoryPath"></param>
    /// <param name="javaInfo"></param>
    private JavaRuntime(string directoryPath, JavaInfo javaInfo)
    {
        DirectoryPath = directoryPath;
        _javaInfo = javaInfo;
    }

    /// <summary>
    /// 具体的 Java 信息数据结构
    /// </summary>
    private class JavaInfo
    {
        public int Version { get; init; }

        public bool Is64Bit { get; init; }

        // public Architecture Architecture { get; set; }
        public bool IsJre { get; init; }
        public bool IsFatFile { get; set; }
        public JavaCompability Compability { get; set; }
        public required string JavaExe { get; set; }
        public required string JavaWExe { get; init; }
    }

    /// <summary>
    /// 单个Java 实体的工厂函数
    /// </summary>
    /// <param name="directoryPath">Java 可执行文件的父目录</param>
    /// <param name="isUserImport">是否为用户手动导入</param>
    /// <returns>得到的 Java 运行时，如果初始化失败会返回 null，需要在外部判断</returns>
    public static async Task<JavaRuntime?> CreateJavaEntityAsync(string directoryPath, bool isUserImport = false)
    {
        Debug.WriteLine($"创建 JavaRuntime: {directoryPath}");
        var javaInfo = await JavaInfoInitAsync(directoryPath);
        if (javaInfo.Compability == JavaCompability.Error) return null;
        var javaEntity = new JavaRuntime(directoryPath, javaInfo) { IsUserImport = isUserImport };
        return javaEntity;
    }

    /// <summary>
    /// 是否为用户手动导入，手动导入的运行时在刷新时不会被刷新掉
    /// </summary>
    public bool IsUserImport { get; set; }

    /// <summary>
    /// Java 数字版本
    /// </summary>
    public int Version => _javaInfo.Version;

    public bool Is64Bit => _javaInfo.Is64Bit;

    /// <summary>
    /// Win11 和 macOS 都有兼具 arm 和 x86_64 兼容的可执行文件，如果是这种通用文件就标记为 True
    /// </summary>
    public bool IsFatFile => _javaInfo.IsFatFile;

    public JavaCompability Compability => _javaInfo.Compability;
    public bool IsJre => _javaInfo.IsJre;
    public string JavaExe => Path.Combine(DirectoryPath, "java");

    /// <summary>
    /// Windows 特有的 javaw.exe
    /// </summary>
    public string JavaWExe => _javaInfo.JavaWExe;

    /// <summary>
    /// 异步地初始化 Java 信息
    /// </summary>
    /// <param name="directoryPath">需要初始化的 Java 所在的父目录</param>
    /// <returns>得到的 Java 具体信息，JavaCompability 为 Error 时为无效</returns>
    private static async Task<JavaInfo> JavaInfoInitAsync(string directoryPath)
    {
        Debug.WriteLine("JavaInfoInit...");
        var javaExe = Path.Combine(directoryPath, "java");
        string runJavaOutput;
        try
        {
            runJavaOutput = await GetRunJavaOutputAsync(javaExe);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return new JavaInfo
            {
                Version = 0,
                Is64Bit = false,
                IsJre = false,
                IsFatFile = false,
                Compability = JavaCompability.Error,
                JavaExe = javaExe,
                JavaWExe = Const.Os is Const.RunningOs.Windows
                    ? Path.Combine(directoryPath, "javaw.exe")
                    : javaExe,
            };
        }

        var info = new JavaInfo
        {
            Version = MatchVersion(runJavaOutput), // 设置版本（Version）
            Is64Bit = MatchIs64Bit(runJavaOutput), // 设置位数（Is64Bit）
            IsJre = !File.Exists(Path.Combine(directoryPath,
                Const.Os is Const.RunningOs.Windows ? "javac.exe" : "javac")),
            // Architecture = RuntimeInformation.OSArchitecture,
            IsFatFile = false,
            Compability = JavaCompability.Unknown,
            JavaExe = javaExe,
            JavaWExe = Const.Os is Const.RunningOs.Windows
                ? Path.Combine(directoryPath, "javaw.exe")
                : javaExe,
        };

        if (info.Version == 0)
            info.Compability = JavaCompability.Error;

        // 针对 Windows 设置兼容性，是 64 位则兼容
        if (Const.Os is Const.RunningOs.Windows)
        {
            info.Compability = info.Is64Bit ? JavaCompability.Yes : JavaCompability.No;
        }

        // 针对 macOS 的转译问题额外设置兼容性
        if (Const.Os is Const.RunningOs.MacOs)
        {
            var sysArchitecture = RuntimeInformation.OSArchitecture;
            byte[] bytes = await File.ReadAllBytesAsync(javaExe);
            uint magic = BitConverter.ToUInt32(bytes, 0);
            if (magic == (uint)Magic.FAT_CIGAM)
            {
                info.IsFatFile = true;
                FatArch[] arches = Fat.loadFatArchs(bytes);
                info.Compability = sysArchitecture switch
                {
                    Architecture.Arm64 => arches.Any(arch => arch.CPUType is CPUType.ARM64) ? JavaCompability.Yes :
                        arches.Any(arch => arch.CPUType is CPUType.X64) ? JavaCompability.UnderTranslation :
                        JavaCompability.Error,
                    Architecture.X64 => arches.Any(arch => arch.CPUType is CPUType.X64)
                        ? JavaCompability.Yes
                        : JavaCompability.No,
                    _ => throw new ArgumentOutOfRangeException(),
                };
            }
            else if (magic == (uint)Magic.FAT_MAGIC)
            {
                info.IsFatFile = true;
                throw new NotImplementedException();
            }
            else
            {
                info.IsFatFile = false;
                var binFile = FileFactory.load(javaExe, bytes, FileFormat.MachBinary, ISA.DefaultISA, null, null);
                var arch = binFile.ISA.Arch;
                info.Compability = sysArchitecture switch
                {
                    Architecture.Arm64 => arch is B2R2.Architecture.AARCH64 ? JavaCompability.Yes :
                        arch is B2R2.Architecture.IntelX64 ? JavaCompability.UnderTranslation : JavaCompability.No,
                    Architecture.X64 => arch is B2R2.Architecture.IntelX64 ? JavaCompability.Yes : JavaCompability.No,
                    _ => throw new ArgumentOutOfRangeException(),
                };
            }
            // using var lipoProcess = new Process();
            // lipoProcess.StartInfo = new ProcessStartInfo
            // {
            //     FileName = "/usr/bin/lipo",
            //     Arguments = "-info " + javaExe,
            //     UseShellExecute = false,
            //     CreateNoWindow = true,
            //     RedirectStandardError = true,
            //     RedirectStandardOutput = true
            // };
            // lipoProcess.Start();
            // await lipoProcess.WaitForExitAsync();
            // var output = (await lipoProcess.StandardOutput.ReadToEndAsync()).Trim();
            // var sysArchitecture = RuntimeInformation.OSArchitecture;
            // info.IsFatFile = !output.StartsWith("Non-fat file");
            // output = output.AfterLast(":");
            // Debug.Assert(sysArchitecture is Architecture.X64 or Architecture.Arm64);
            // switch (sysArchitecture)
            // {
            //     case Architecture.X64:
            //         info.Compability = output.Contains("x86_64") ? JavaCompability.Yes : JavaCompability.No;
            //         break;
            //     case Architecture.Arm64:
            //         if (output.Contains("arm64")) info.Compability = JavaCompability.Yes;
            //         else if (output.Contains("x86_64")) info.Compability = JavaCompability.UnderTranslation;
            //         break;
            //     default:
            //         Debug.Fail("本句报错理论上永远不会出现：可运行Avalonia的macOS不可能是其他架构");
            //         break;
            // }
        }

        // 针对 Linux 设置兼容性
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // using var fileProcess = new Process();
            // fileProcess.StartInfo = new ProcessStartInfo
            // {
            //     FileName = "/usr/bin/file",
            //     Arguments = "-L " + JavaExe,
            //     UseShellExecute = false,
            //     RedirectStandardOutput = true
            // };
            // fileProcess.Start();
            // fileProcess.WaitForExit();
            // var arch = fileProcess.StandardOutput.ReadToEnd().Trim().Replace(JavaExe, "").Split(",")[1];
            // info.IsFatFile = false; // TODO 需要进一步判断
            // switch (RuntimeInformation.OSArchitecture)
            // {
            //     case Architecture.X64:
            //         info.Compability = arch.Contains("x86-64") ? JavaCompability.Yes : JavaCompability.No;
            //         break;
            //     case Architecture.Arm64:
            //         if (arch.Contains("ARM aarch64"))
            //             info.Compability = JavaCompability.Yes;
            //         else if (arch.Contains("x86-64"))
            //             info.Compability = JavaCompability.UnderTranslation; // QEMU
            //         break;
            //     default:
            //         Debug.WriteLine("未知的 Linux 系统架构");
            //         break;
            // }

            await using FileStream fs = new(javaExe, FileMode.Open, FileAccess.Read);
            using BinaryReader reader = new(fs);
            if (reader.ReadByte() == 0x7F &&
                reader.ReadByte() == 'E' &&
                reader.ReadByte() == 'L' &&
                reader.ReadByte() == 'F')
            {
                fs.Seek(12, SeekOrigin.Current);

                ushort eMachine = reader.ReadUInt16();

                Architecture? architecture = eMachine switch
                {
                    0x03 => Architecture.X86,
                    0x3E => Architecture.X64,
                    0x28 => Architecture.Arm,
                    0xB7 => Architecture.Arm64,
                    0xF3 => Architecture.RiscV64,
                    0x102 => Architecture.LoongArch64,
                    // TODO 添加更多的架构判断
                    _ => null
                };
                if (architecture != null)
                {
                    Console.WriteLine($"{javaExe}: {architecture.Value}"); // for debug
                    info.Compability = architecture.Value == RuntimeInformation.OSArchitecture
                        ? JavaCompability.Yes
                        : JavaCompability.No; // 未判断转译
                }
            }
        }

        // TODO)) 判断其他系统的可执行文件架构
        return info;
    }

    /// <summary>
    /// 刷新 Java 实体的信息
    /// </summary>
    public async Task<bool> RefreshInfo()
    {
        _javaInfo = await JavaInfoInitAsync(DirectoryPath);
        return _javaInfo.Compability != JavaCompability.Error;
    }

    /// <summary>
    /// 运行 java -version 并获取输出
    /// </summary>
    /// <returns></returns>
    private static async Task<string> GetRunJavaOutputAsync(string javaExe)
    {
        using var javaProcess = new Process();
        javaProcess.StartInfo = new ProcessStartInfo
        {
            FileName = javaExe,
            Arguments = "-version",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true, // 这个Java的输出流是tmd stderr！！！
            RedirectStandardOutput = true
        };
        javaProcess.Start();
        await javaProcess.WaitForExitAsync();

        var output = await javaProcess.StandardError.ReadToEndAsync();
        return output != string.Empty ? output : await javaProcess.StandardOutput.ReadToEndAsync(); // 就是tmd stderr
    }

    private static int MatchVersion(string runJavaOutput)
    {
        var regexMatch = Regex.Match(runJavaOutput, """version\s+"([\d._]+)""");
        var match = Regex.Match(regexMatch.Success ? regexMatch.Groups[1].Value : string.Empty, @"^(\d+)");
        int version = match.Success ? int.Parse(match.Groups[1].Value) : 0;
        if (version == 1)
        {
            // java version 8
            match = Regex.Match(regexMatch.Groups[1].Value, @"^1\.(\d+)\.");
            version = match.Success ? int.Parse(match.Groups[1].Value) : 0;
        }

        return version;
    }

    private static bool MatchIs64Bit(string runJavaOutput)
    {
        var regexMatch = Regex.Match(runJavaOutput, @"\b(\d+)-Bit\b"); // get bit
        return (regexMatch.Success ? regexMatch.Groups[1].Value : string.Empty) == "64";
    }
}