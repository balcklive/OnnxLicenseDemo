// 机器指纹：把 CPU 序列号 + 主板序列号 拼起来做 SHA-256，作为这台机器的唯一 ID。
// 这个 ID 会被写进许可证里，换一台机器就无法使用同一份许可证。
//
// 注意：
//   1. 这里用 WMI，仅支持 Windows。要做跨平台（Linux/Mac）的话，
//      Linux 可以读 /etc/machine-id，Mac 可以用 `ioreg -rd1 -c IOPlatformExpertDevice`
//      拿到 IOPlatformUUID，再走同样的哈希逻辑即可。
//   2. 少数虚拟机/云主机的 WMI 序列号是空的或者所有克隆实例都一样，
//      生产环境建议再叠加一个"首次启动时生成并写入注册表/文件"的随机 GUID 作为兜底。

using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace ClientApp;

public static class MachineFingerprint
{
    public static string Get()
    {
        string cpuId = QueryWmi("Win32_Processor", "ProcessorId");
        string boardSerial = QueryWmi("Win32_BaseBoard", "SerialNumber");

        var raw = $"{cpuId}|{boardSerial}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash); // 64个十六进制字符
    }

    private static string QueryWmi(string wmiClass, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var obj in searcher.Get())
            {
                var value = obj[property]?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        catch
        {
            // 某些环境（容器、部分虚拟机）可能查不到，降级为空字符串而不是整个程序崩溃
        }
        return "unknown";
    }
}
