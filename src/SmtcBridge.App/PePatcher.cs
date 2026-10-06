using System.Buffers.Binary;

namespace SmtcBridge;

/// <summary>
/// 给 PotPlayer 主程序打「导入表补丁」：往它的导入表里加一条
/// `PotPlayerSmtcHook.dll!PotPlayerSmtcBridgeAttach`，这样 Windows 加载器
/// 每次启动 PotPlayer 都会自动把我们的 DLL 载入并执行 DllMain。
///
/// 效果：注册一次，之后不需要任何常驻进程、不需要开机运行任何东西，
/// 重启也照样生效。PotPlayer 更新会覆盖掉这个补丁，需要重新打一次。
///
/// 做法：不新增节表项之外的结构——在文件末尾追加一个新节，
/// 把「原有导入描述符 + 我们这条 + 终止项」以及配套的 INT/IAT/名称串放进去，
/// 再把数据目录第 1 项（IMPORT）指向新表。原节内容一律不动。
/// </summary>
internal static class PePatcher
{
    private const string HookDllName = "PotPlayerSmtcHook.dll";
    private const string ExportName = "PotPlayerSmtcBridgeAttach";
    public const string BackupSuffix = ".smtb-backup";

    // PE 结构里用到的偏移常量
    private const int PeSignatureOffsetInDos = 0x3C;
    private const int CoffHeaderSize = 20;
    private const int SectionHeaderSize = 40;
    private const int ImportDirectoryIndex = 1;

    public static string BackupPathFor(string exePath) => exePath + BackupSuffix;
    public static bool HasBackup(string exePath) => File.Exists(BackupPathFor(exePath));

    /// <summary>补丁是否已经打过（用导入表里的 DLL 名字判断）。</summary>
    public static bool IsPatched(string exePath)
    {
        if (!File.Exists(exePath)) return false;
        try
        {
            var bytes = File.ReadAllBytes(exePath);
            var needle = System.Text.Encoding.ASCII.GetBytes(HookDllName);
            return IndexOf(bytes, needle) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>打补丁。会先备份成 &lt;exe&gt;.smtb-backup（只备份一次，保留最原始的版本）。</summary>
    public static void Install(string exePath)
    {
        // v0.6.3 起停用（Restore 仍可用）：
        // 实测 PotPlayerMini64.exe 是 Themida(WinLicense) 加壳程序，会自查文件长度与内容，
        // 任何改动都会让它拒绝启动并弹出 “Cannot find or init PotPlayer64.dll”；
        // PotPlayer64.dll / MediaDB64.dll 又都带 Kakao 数字签名并被 WinVerifyTrust 校验，
        // 替身 DLL 会被判定为 “modified or hacked”。
        // 结论：不能再改 PotPlayer 的任何文件，安装方式改为 IFEO 启动注入。
        throw new NotSupportedException(
            "主程序补丁方式已停用：PotPlayer 主程序带自校验，改文件会导致它无法启动。" +
            "新的安装方式改为 IFEO 启动注入，不改动任何 PotPlayer 文件。");
    }

    /// <summary>旧的打补丁实现（已停用，仅留作历史排查与参考）。</summary>
    private static void InstallLegacy(string exePath)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("找不到目标程序", exePath);
        if (IsPatched(exePath))
            return;

        var backup = BackupPathFor(exePath);
        if (!File.Exists(backup)) File.Copy(exePath, backup);

        var original = File.ReadAllBytes(exePath);
        var patched = Patch(original);

        var temp = exePath + ".smtb-tmp";
        File.WriteAllBytes(temp, patched);
        File.Replace(temp, exePath, destinationBackupFileName: null);

        // 补丁只是让主程序"去找 PotPlayerSmtcHook.dll"，这个 DLL 必须和主程序同目录。
        // 漏了这一步的表现是：点 PotPlayer 毫无反应（加载器找不到 DLL，进程直接失败）。
        var targetDir = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrEmpty(targetDir) && File.Exists(AppPaths.HookDll))
        {
            File.Copy(AppPaths.HookDll, Path.Combine(targetDir, HookDllName), overwrite: true);
        }
    }

    /// <summary>还原：把备份覆盖回去。</summary>
    public static void Restore(string exePath)
    {
        var backup = BackupPathFor(exePath);
        if (!File.Exists(backup))
            throw new FileNotFoundException("没有找到备份文件", backup);
        File.Copy(backup, exePath, overwrite: true);

        // 顺手把一起放进去的 DLL 也清掉，别在主程序目录里留垃圾
        var targetDir = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrEmpty(targetDir))
        {
            var installed = Path.Combine(targetDir, HookDllName);
            if (File.Exists(installed)) File.Delete(installed);
        }
    }

    /// <summary>补丁和 DLL 是否都已就位（只看补丁不算装好）。</summary>
    public static bool IsFullyInstalled(string exePath)
    {
        if (!IsPatched(exePath)) return false;
        var dir = Path.GetDirectoryName(exePath);
        return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, HookDllName));
    }

    // ---------------------------------------------------------------- 实际补丁

    private static byte[] Patch(byte[] file)
    {
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(PeSignatureOffsetInDos));
        if (peOffset <= 0 || peOffset + 4 + CoffHeaderSize > file.Length)
            throw new InvalidDataException("不是有效的 PE 文件（e_lfanew 越界）");
        if (file[peOffset] != 'P' || file[peOffset + 1] != 'E' || file[peOffset + 2] != 0 ||
            file[peOffset + 3] != 0)
            throw new InvalidDataException("不是有效的 PE 文件（签名不匹配）");

        var coff = peOffset + 4;
        var numberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(coff + 2));
        var sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(coff + 16));
        var optional = coff + CoffHeaderSize;

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(optional));
        var is64 = magic == 0x20B;
        if (magic != 0x20B && magic != 0x10B)
            throw new InvalidDataException($"不支持的可选头 Magic=0x{magic:X}");
        var pointerSize = is64 ? 8 : 4;

        var sectionAlignment = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(optional + 32));
        var fileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(optional + 36));
        var sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(optional + 56));
        var sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(optional + 60));
        var dataDirectories = optional + (is64 ? 112 : 96);
        var sectionsOffset = optional + sizeOfOptionalHeader;

        // ---- 读取节表 ----
        var lastVirtualEnd = 0u;
        var lastRawEnd = 0u;
        var sectionRanges = new List<(uint Rva, uint VirtualSize, uint RawPtr, uint RawSize)>();

        for (var i = 0; i < numberOfSections; i++)
        {
            var s = sectionsOffset + i * SectionHeaderSize;
            var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(s + 8));
            var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(s + 12));
            var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(s + 16));
            var rawPtr = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(s + 20));
            sectionRanges.Add((virtualAddress, virtualSize, rawPtr, rawSize));
            lastVirtualEnd = Math.Max(lastVirtualEnd, virtualAddress + virtualSize);
            lastRawEnd = Math.Max(lastRawEnd, rawPtr + rawSize);
        }

        // ---- 复制原有导入描述符 ----
        var importDirRva = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(dataDirectories +
            ImportDirectoryIndex * 8));
        if (importDirRva == 0)
            throw new InvalidDataException("目标程序没有导入目录，无法打补丁");
        var importDirOffset = RvaToOffset(sectionRanges, importDirRva);

        var descriptors = new List<byte[]>();
        var cursor = importDirOffset;
        while (true)
        {
            var descriptor = new byte[20];
            Array.Copy(file, cursor, descriptor, 0, 20);
            var allZero = true;
            foreach (var b in descriptor)
            {
                if (b == 0) continue;
                allZero = false;
                break;
            }
            if (allZero) break;
            descriptors.Add(descriptor);
            cursor += 20;
            if (descriptors.Count > 256) throw new InvalidDataException("导入描述符异常");
        }

        // ---- 先算出内容需要多大（与放在哪个 RVA 无关）----
        var descriptorsSize = (uint)((descriptors.Count + 2) * 20);   // + 我们这条 + 终止项
        var intSize = (uint)(pointerSize * 2);                        // 一项 + 终止项
        var nameSize = (uint)HookDllName.Length + 1;
        var hintNameSize = (uint)(2 + ExportName.Length + 1);
        var layoutSize = descriptorsSize + intSize * 2 + nameSize + hintNameSize + 16;

        // ---- 选一个「宿主节」来承载新数据 ----
        //
        // 首选：某个节的 SizeOfRawData 比 VirtualSize 大，尾部有空隙——
        // 这类空隙已经映射在映像里，把内容放进去既不用改文件长度，也不用动节表结构。
        // `.idata`（导入节）通常就是这种，而且是正经的初始化数据节。
        // 次选：把内容追加到文件末尾（需要撑大最后一个节）。
        var hostIndex = -1;
        var useSlack = false;
        var allowSlack = Environment.GetEnvironmentVariable("SMTB_PATCH_NOSLACK") != "1";
        for (var i = 0; allowSlack && i < sectionRanges.Count; i++)
        {
            // 必须用带符号比较：RawSize < VirtualSize 的节很常见，无符号相减会下溢，
            // 把"完全没有空隙"判成"空隙巨大"，于是把导入表写到节的原始数据之外，
            // 加载器读到无效内存 → 进程 0xC0000005。
            var slack = (long)sectionRanges[i].RawSize - sectionRanges[i].VirtualSize;
            if (sectionRanges[i].RawPtr == 0 || slack < layoutSize) continue;
            hostIndex = i;
            useSlack = true;
            break;
        }

        if (hostIndex < 0)
        {
            var hostRawEnd = 0u;
            for (var i = 0; i < sectionRanges.Count; i++)
            {
                var end = sectionRanges[i].RawPtr + sectionRanges[i].RawSize;
                if (end <= hostRawEnd) continue;
                hostRawEnd = end;
                hostIndex = i;
            }
        }
        if (hostIndex < 0) throw new InvalidDataException("目标程序没有可用的节");

        var host = sectionRanges[hostIndex];
        uint newRawPtr;
        uint newRva;

        if (useSlack)
        {
            // 放在节内空隙：RVA 紧接在原有虚拟内容之后，文件偏移随之对应
            newRva = Align(host.Rva + host.VirtualSize, 4);
            newRawPtr = host.RawPtr + (newRva - host.Rva);
        }
        else
        {
            // 照 CFF Explorer 的做法（实测可用）：
            //   新数据的 RVA 对齐到 SectionAlignment，而不是只按文件对齐；
            //   文件末尾那段不属于任何节的叠加数据（MinGW 的 COFF 符号表之类）
            //   直接截掉——既腾出空间，也让节的虚拟大小保持常规。
            newRva = Align(host.Rva + host.VirtualSize, sectionAlignment);
            newRawPtr = host.RawPtr + (newRva - host.Rva);
        }

        if (Environment.GetEnvironmentVariable("SMTB_PATCH_DEBUG") == "1")
        {
            Console.Error.WriteLine(
                $"[pe] fileLen={file.Length} host#{hostIndex} slackMode={useSlack} Rva=0x{host.Rva:X} " +
                $"VSize=0x{host.VirtualSize:X} RawPtr=0x{host.RawPtr:X} RawSize=0x{host.RawSize:X} " +
                $"need={layoutSize} newRawPtr=0x{newRawPtr:X} newRva=0x{newRva:X}");
        }

        // ---- 在新数据区里排布内容 ----
        //
        // 顺序照抄 CFF Explorer 实测可用的产物：
        //   描述符 → DLL 名 → hint + 函数名 → IAT → INT
        // 而且 hint 那 2 个字节直接复用 DLL 名结尾的 \0，整体是紧凑排布的。
        var nameRva = newRva + descriptorsSize;
        // 注意：hint/name 紧跟在名字的结束符之后，不要和它重叠——
        // 之前写成 nameSize - 1 会让整块前移一个字节，加载器会直接忽略这条导入。
        var hintNameRva = nameRva + nameSize;
        var iatRva = hintNameRva + hintNameSize;
        var intRva = iatRva + intSize;
        var sectionVirtualSize = intRva + intSize - newRva;
        var sectionRawSize = useSlack ? 0u : Align(sectionVirtualSize, fileAlignment);
        var hintNameOffset = hintNameRva - newRva;
        var iatOffset = iatRva - newRva;
        var intOffset = intRva - newRva;

        var contentSize = useSlack ? sectionVirtualSize : sectionRawSize;
        var content = new byte[contentSize];

        for (var i = 0; i < descriptors.Count; i++)
        {
            Array.Copy(descriptors[i], 0, content, i * 20, 20);
        }

        // 我们这条描述符，紧跟在原有描述符之后（终止项由 content 的零值充当）
        var ours = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(ours.AsSpan(0), intRva);        // OriginalFirstThunk
        BinaryPrimitives.WriteUInt32LittleEndian(ours.AsSpan(12), nameRva);      // Name
        BinaryPrimitives.WriteUInt32LittleEndian(ours.AsSpan(16), iatRva);       // FirstThunk
        Array.Copy(ours, 0, content, descriptors.Count * 20, 20);

        // DLL 名（结尾 NUL 同时充当 hint 的第一个字节）
        System.Text.Encoding.ASCII.GetBytes(HookDllName).CopyTo(content, (int)(nameRva - newRva));

        // hint + 函数名
        BinaryPrimitives.WriteUInt16LittleEndian(content.AsSpan((int)hintNameOffset), 0);
        System.Text.Encoding.ASCII.GetBytes(ExportName).CopyTo(content, (int)hintNameOffset + 2);

        // INT 第一项指向 hint/name，第二项为 0；IAT 全零（加载器回填）
        if (pointerSize == 8)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan((int)intOffset), hintNameRva);
            // IAT 的初值必须和 INT 一样——这是链接器的惯例（也是 CFF 的产物写法）。
            // 留全零看着"等价"，但实测加载器会因此整条导入都不处理。
            BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan((int)iatOffset), hintNameRva);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan((int)intOffset), hintNameRva);
            BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan((int)iatOffset), hintNameRva);
        }

        // ---- 组装新文件 ----
        // 绝不截断文件！
        // 之前照 CFF 的做法把文件末尾"不属于任何节"的叠加数据切掉，
        // 结果真实 PotPlayer 主程序切完之后起不来（报 PotPlayer64.dll 初始化失败）。
        // 现在改成：把插入点之后的原有内容整体后移，一个字节都不丢。
        // 原地写得下就一个字节都别动：PotPlayer 主程序会自查文件长度，
        // 长度一变直接拒绝启动（Cannot find or init PotPlayer64.dll）。
        var fitsInPlace = newRawPtr + contentSize <= (uint)file.Length;
        var needsShift = !useSlack && !fitsInPlace && newRawPtr < (uint)file.Length;
        var outputSize = Math.Max((uint)file.Length,
                                  needsShift ? newRawPtr + contentSize + ((uint)file.Length - newRawPtr)
                                             : newRawPtr + contentSize);
        var output = new byte[outputSize];
        Array.Copy(file, output, Math.Min(file.Length, (int)outputSize));

        if (needsShift)
        {
            // 把 [newRawPtr, EOF) 的原有数据搬到我们内容之后
            var tailLength = file.Length - (int)newRawPtr;
            Array.Copy(file, (int)newRawPtr, output, (int)(newRawPtr + contentSize), tailLength);
        }

        if (!useSlack)
        {
            // 宿主节被撑大之后，原来落在文件末尾的叠加数据（MinGW 的 COFF 符号表等）
            // 会变成这个节"原始数据"的一部分被映射进内存。照 CFF 的做法清零，
            // 别把无关内容留在映像里。
            var gapStart = host.RawPtr + host.RawSize;
            if (newRawPtr > gapStart)
            {
                Array.Clear(output, (int)gapStart, (int)(newRawPtr - gapStart));
            }
        }
        Array.Copy(content, 0, output, newRawPtr, content.Length);

        // 宿主节：扩大虚拟大小与原始数据大小，并允许写入
        var hostHeader = sectionsOffset + hostIndex * SectionHeaderSize;
        var newHostVirtualSize = (newRva - host.Rva) + sectionVirtualSize;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(hostHeader + 8), newHostVirtualSize);
        if (!useSlack)
        {
            // 只有"追加到文件末尾"这种模式才需要改 SizeOfRawData；
            // 用节内空隙时原有的 RawSize 已经覆盖得到。
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(hostHeader + 16),
                newRawPtr - host.RawPtr + sectionRawSize);
        }
        var hostCharacteristics =
            BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(hostHeader + 36));
        // 宿主节常常是 .reloc 这类被标记为 DISCARDABLE 的节，
        // 加载器在解析导入时可能已经把它丢掉了——必须清掉这个标记，
        // 同时补上 MEM_WRITE（IAT 需要回填）。
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(hostHeader + 36),
            (hostCharacteristics | 0x80000000) & ~0x02000000u);

        // SizeOfImage 必须覆盖被撑大的宿主节，否则加载器会认为新数据不在映像里
        var newSizeOfImage = Align(host.Rva + newHostVirtualSize, sectionAlignment);
        if (newSizeOfImage < sizeOfImage) newSizeOfImage = sizeOfImage;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(optional + 56), newSizeOfImage);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dataDirectories +
            ImportDirectoryIndex * 8), newRva);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(dataDirectories +
            ImportDirectoryIndex * 8 + 4), descriptorsSize);

        return output;
    }

    private static uint RvaToOffset(List<(uint Rva, uint VirtualSize, uint RawPtr, uint RawSize)> sections,
        uint rva)
    {
        foreach (var (sectionRva, virtualSize, rawPtr, rawSize) in sections)
        {
            if (rva >= sectionRva && rva < sectionRva + Math.Max(virtualSize, rawSize))
                return rawPtr + (rva - sectionRva);
        }
        throw new InvalidDataException($"RVA 0x{rva:X} 不在任何节里");
    }

    private static uint Align(uint value, uint alignment)
    {
        if (alignment == 0) return value;
        return (value + alignment - 1) / alignment * alignment;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j]) continue;
                match = false;
                break;
            }
            if (match) return i;
        }
        return -1;
    }
}
