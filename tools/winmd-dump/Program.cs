// 从 Windows 元数据（winmd）里读取 WinRT 类型的接口方法顺序与 GUID。
//
// 用途：调用 SMTC 的写接口需要知道方法在 vtable 中的位置。凭记忆写有静默失败的风险，
// 这个工具从系统自带的 Windows.Media.winmd 里把权威定义读出来。
//
// 运行：
//   dotnet run tools/winmd-dump/Program.cs
//   dotnet run tools/winmd-dump/Program.cs C:\Windows\System32\WinMetadata\Windows.Media.winmd MusicDisplayProperties

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

string path = args.Length > 0 ? args[0] : @"C:\Windows\System32\WinMetadata\Windows.Media.winmd";
string[] filters = args.Length > 1
    ? args[1..]
    : ["SystemMediaTransportControls", "MusicDisplayProperties"];

if (!File.Exists(path))
{
    Console.Error.WriteLine($"找不到文件: {path}");
    return 1;
}

using var stream = File.OpenRead(path);
using var pe = new PEReader(stream);
var md = pe.GetMetadataReader();

int printed = 0;
foreach (var handle in md.TypeDefinitions)
{
    var td = md.GetTypeDefinition(handle);
    string ns = md.GetString(td.Namespace);
    string name = md.GetString(td.Name);

    if (!filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase))) continue;

    printed++;
    Console.WriteLine($"=== {ns}.{name} ===");
    Console.WriteLine($"    attrs: {td.Attributes}");

    foreach (var caHandle in td.GetCustomAttributes())
    {
        var ca = md.GetCustomAttribute(caHandle);
        string ctorName = ca.Constructor.Kind switch
        {
            HandleKind.MemberReference => md.GetString(md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Name),
            HandleKind.MethodDefinition => md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).Name),
            _ => "?",
        };
        if (!ctorName.Contains("Guid", StringComparison.OrdinalIgnoreCase)) continue;

        var blob = md.GetBlobReader(ca.Value);
        var raw = blob.ReadBytes(blob.RemainingBytes);
        Console.WriteLine($"    {ctorName} blob ({raw.Length} bytes): {Convert.ToHexString(raw)}");
        if (raw.Length >= 16) Console.WriteLine($"    guid guess: {new Guid(raw.AsSpan(0, 16))}");
    }

    foreach (var ih in td.GetInterfaceImplementations())
    {
        var ii = md.GetInterfaceImplementation(ih);
        Console.WriteLine($"    implements: {Describe(md, ii.Interface)}");
    }

    Console.WriteLine("    methods (own, declaration order):");
    int slot = 0;
    foreach (var mh in td.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        Console.WriteLine($"      [{slot,2}] {md.GetString(m.Name)}");
        slot++;
    }
    Console.WriteLine();
}

if (printed == 0)
{
    Console.WriteLine("没有匹配的类型。可用过滤词示例：");
    Console.WriteLine("  SystemMediaTransportControls / MusicDisplayProperties / TimelineProperties");
}
return 0;

static string Describe(MetadataReader md, EntityHandle handle)
{
    switch (handle.Kind)
    {
        case HandleKind.TypeReference:
        {
            var tr = md.GetTypeReference((TypeReferenceHandle)handle);
            return $"{md.GetString(tr.Namespace)}.{md.GetString(tr.Name)}";
        }
        case HandleKind.TypeDefinition:
        {
            var td = md.GetTypeDefinition((TypeDefinitionHandle)handle);
            return $"{md.GetString(td.Namespace)}.{md.GetString(td.Name)}";
        }
        default:
            return handle.Kind.ToString();
    }
}
