// ClientApp: 最终交付给客户的软件里应包含的核心逻辑演示（联网激活 + 心跳版）。
// 实际项目中把这几步嵌入你的 C# WinForms/WPF/控制台程序的启动流程即可。

using ClientApp;

// 把这里换成你自己部署好的授权服务器地址
const string ServerBaseUrl = "https://your-domain.com";
const string EncryptedModelPath = "model.onnx.enc";

if (args.Length > 0 && args[0] == "--print-fingerprint")
{
    Console.WriteLine(MachineFingerprint.Get());
    return;
}

Console.WriteLine("正在验证授权...");
var client = new OnlineLicenseClient(ServerBaseUrl);
var result = await client.EnsureActivatedAsync();

if (!result.Ok)
{
    Console.WriteLine($"[拒绝启动] {result.Reason}");
    Environment.Exit(1);
    return;
}

Console.WriteLine($"授权有效（{result.Reason}），产品: {result.ProductName}，激活码: {result.LicenseCode}");

var modelKey = Convert.FromBase64String(result.ModelKeyBase64!);

Console.WriteLine("正在解密并加载模型...");
using var session = SecureModelLoader.LoadSession(EncryptedModelPath, modelKey);

Console.WriteLine("模型加载成功！输入节点：");
foreach (var input in session.InputMetadata)
{
    Console.WriteLine($"  - {input.Key}: {string.Join("x", input.Value.Dimensions)}");
}

Console.WriteLine("（在这里接你真正的推理逻辑：session.Run(...)）");
