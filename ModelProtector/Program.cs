// ModelProtector
// ------------------------------------------------------------
// 用 AES-256-GCM 加密 .onnx 模型文件。GCM 是带认证的加密模式：除了保密还防篡改，
// 密钥不对或密文被改过都会在解密时直接抛异常。
//
// 容器格式 BSMO v1（BotCs Secure MOdel）：
//   [0,4)   magic "BSMO"
//   [4]     版本号 = 1
//   [5,8)   保留 = 0
//   [8,20)  nonce (12 字节)
//   [20,36) tag (16 字节)
//   [36,)   密文
//
// 为什么要 magic：交付包里的模型**文件名保持不变**（还是 xxx.onnx），客户端靠文件头判断
// 这是密文还是明文，于是开发机的明文模型和交付包的密文能共用同一条加载路径
// （见 bot-cs 的 ModelVault）。ONNX 是 protobuf，第一字节恒为 0x08，不可能撞上 "BSMO"，
// 所以这个判定是无歧义的。
//
// 向后兼容：读侧仍接受本工具 2026-09-24 之前的旧格式（无 magic：[12 nonce][16 tag][密文]）。
//
// 用法：
//   keygen      [--out <path>]
//   encrypt     --in <file> --out <file> [--key <b64> | --keyfile <path>]
//       不给密钥就自动生成并打印（妥善保存，签发许可证时要用同一把）
//   decrypt     --in <file> --out <file> --key <b64> | --keyfile <path>
//       仅本地测试用；正式客户端是解密到内存，不落盘
//   encrypt-dir --dir <root> [--pattern *.onnx] (--key <b64> | --keyfile <path>) [--dry-run]
//       **原地**递归加密：文件名不变、内容换成密文。发布流水线用。必须显式给密钥，
//       本命令绝不自动生成——一批文件共用同一把密钥（"一个产品一把 AES"的约定）。
//       已经是密文的文件跳过，所以重复跑是安全的。
//   verify-dir  --dir <root> [--pattern *.onnx] [--key <b64> | --keyfile <path>]
//       断言目录里每个匹配文件都是密文；给了密钥就逐个真解密（能抓出"用错密钥加密"
//       这种要到客户机上才暴露的错）。有任何明文或解密失败 → 退出码 1。
//   inspect     --in <file>
//
// 退出码：0 成功；1 校验/断言失败；2 参数错误。

using System.Security.Cryptography;
using System.Text;

const string Magic = "BSMO";
const byte ContainerVersion = 1;
const int NonceLen = 12;
const int TagLen = 16;
const int HeaderLen = 36;
const int LegacyHeaderLen = 28;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

var command = args[0];
byte[]? key;
try
{
    key = ReadKey(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("密钥参数有误: " + ex.Message);
    return 2;
}

try
{
    switch (command)
    {
        case "keygen":
            return KeyGen(args);
        case "encrypt":
            Encrypt(args, key);
            return 0;
        case "decrypt":
            Decrypt(args, RequireKey(args));
            return 0;
        case "encrypt-dir":
            EncryptDir(args, RequireKey(args));
            return 0;
        case "verify-dir":
            return VerifyDir(args, key);
        case "inspect":
            Inspect(args);
            return 0;
        default:
            PrintUsage();
            return 2;
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine("参数错误: " + ex.Message);
    return 2;
}
catch (Exception ex) when (ex is not FileNotFoundException)
{
    Console.Error.WriteLine("失败: " + ex.Message);
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("用法（完整说明见本文件顶部注释）：");
    Console.WriteLine("  keygen      [--out <path>]");
    Console.WriteLine("  encrypt     --in <f> --out <f> [--key <b64> | --keyfile <p>]");
    Console.WriteLine("  decrypt     --in <f> --out <f> --key <b64> | --keyfile <p>");
    Console.WriteLine("  encrypt-dir --dir <root> [--pattern *.onnx] --key|--keyfile [--dry-run]");
    Console.WriteLine("  verify-dir  --dir <root> [--pattern *.onnx] [--key|--keyfile]");
    Console.WriteLine("  inspect     --in <f>");
}

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static string Require(string[] args, string name) =>
    GetArg(args, name) ?? throw new ArgumentException("缺少参数 " + name);

static bool HasFlag(string[] args, string name) => args.Contains(name);

static byte[] ReadKeyFile(string path)
{
    var text = File.ReadAllText(path).Trim();
    return Convert.FromBase64String(text);
}

/// <summary>取 --key / --keyfile；两者都没给返回 null（只有 encrypt 和 keygen 允许不给）。</summary>
static byte[]? ReadKey(string[] args)
{
    byte[]? key = null;
    var inline = GetArg(args, "--key");
    var file = GetArg(args, "--keyfile");
    if (inline != null) key = Convert.FromBase64String(inline.Trim());
    else if (file != null) key = ReadKeyFile(file);
    if (key is null) return null;
    if (key.Length != 32) throw new ArgumentException("密钥必须是 32 字节 (AES-256)，当前 " + key.Length + " 字节");
    return key;
}

static byte[] RequireKey(string[] args) =>
    ReadKey(args) ?? throw new ArgumentException("本命令必须显式给密钥：--key <base64> 或 --keyfile <path>");

static bool HasMagic(byte[] all) =>
    all.Length >= HeaderLen && Encoding.ASCII.GetString(all, 0, Magic.Length) == Magic;

static byte[] BuildContainer(byte[] nonce, byte[] tag, byte[] cipher)
{
    var output = new byte[HeaderLen + cipher.Length];
    Encoding.ASCII.GetBytes(Magic).CopyTo(output, 0);
    output[Magic.Length] = ContainerVersion;
    nonce.CopyTo(output, 8);
    tag.CopyTo(output, 20);
    cipher.CopyTo(output, HeaderLen);
    return output;
}

static (byte[] Nonce, byte[] Tag, byte[] Cipher, bool IsContainer) Unwrap(byte[] all)
{
    if (HasMagic(all))
    {
        if (all[4] != ContainerVersion)
            throw new InvalidDataException("不支持的容器版本 " + all[4] + "（本工具只认 v1）");
        return (all.AsSpan(8, NonceLen).ToArray(), all.AsSpan(20, TagLen).ToArray(),
                all.AsSpan(HeaderLen).ToArray(), true);
    }
    if (all.Length > LegacyHeaderLen)
    {
        // 旧格式（无 magic）：[12 nonce][16 tag][密文]。读侧保留兼容，写侧不再产出。
        return (all.AsSpan(0, NonceLen).ToArray(), all.AsSpan(NonceLen, TagLen).ToArray(),
                all.AsSpan(LegacyHeaderLen).ToArray(), false);
    }
    throw new InvalidDataException("不是本工具加密的模型容器（也没有旧格式的长度）");
}

static byte[] DecryptToPlaintext(byte[] container, byte[] key)
{
    var (nonce, tag, cipher, isContainer) = Unwrap(container);
    if (!isContainer) throw new InvalidDataException("文件里没有 BSMO 容器头，是明文模型");
    var plaintext = new byte[cipher.Length];
    using var aes = new AesGcm(key, TagLen);
    aes.Decrypt(nonce, cipher, tag, plaintext);
    return plaintext;
}

static byte[] EncryptFileContent(byte[] plaintext, byte[] key)
{
    var nonce = RandomNumberGenerator.GetBytes(NonceLen);
    var cipher = new byte[plaintext.Length];
    var tag = new byte[TagLen];
    using (var aes = new AesGcm(key, TagLen))
    {
        aes.Encrypt(nonce, plaintext, cipher, tag);
    }
    return BuildContainer(nonce, tag, cipher);
}

static int KeyGen(string[] args)
{
    var key = RandomNumberGenerator.GetBytes(32);
    var b64 = Convert.ToBase64String(key);
    var outPath = GetArg(args, "--out");
    if (outPath is null)
    {
        Console.WriteLine(b64);
        return 0;
    }
    File.WriteAllText(outPath, b64 + Environment.NewLine);
    Console.WriteLine("已写出密钥 -> " + outPath + "（这个文件就是产品本体资产的全部秘密，别提交、别外发）");
    return 0;
}

static void Encrypt(string[] args, byte[]? key)
{
    var inPath = Require(args, "--in");
    var outPath = Require(args, "--out");
    if (key is null)
    {
        key = RandomNumberGenerator.GetBytes(32);
        Console.WriteLine("未指定 --key/--keyfile，已生成新密钥（签发许可证时要用同一把）：");
        Console.WriteLine("  " + Convert.ToBase64String(key));
    }

    var plaintext = File.ReadAllBytes(inPath);
    File.WriteAllBytes(outPath, EncryptFileContent(plaintext, key));
    Console.WriteLine("已加密 -> " + outPath + " (" + plaintext.Length + " 字节明文 → " +
                      new FileInfo(outPath).Length + " 字节 BSMO 容器)");
}

static void Decrypt(string[] args, byte[] key)
{
    var inPath = Require(args, "--in");
    var outPath = Require(args, "--out");
    var plaintext = DecryptToPlaintext(File.ReadAllBytes(inPath), key);
    try
    {
        File.WriteAllBytes(outPath, plaintext);
    }
    finally
    {
        Array.Clear(plaintext, 0, plaintext.Length);
    }
    Console.WriteLine("已解密 -> " + outPath);
}

static IEnumerable<string> MatchedFiles(string[] args)
{
    var root = Path.GetFullPath(Require(args, "--dir"));
    var pattern = GetArg(args, "--pattern") ?? "*.onnx";
    if (!Directory.Exists(root)) throw new DirectoryNotFoundException("目录不存在: " + root);
    return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static void EncryptDir(string[] args, byte[] key)
{
    var dryRun = HasFlag(args, "--dry-run");
    var encrypted = 0;
    var skipped = 0;
    var longTotal = 0L;

    foreach (var path in MatchedFiles(args))
    {
        var all = File.ReadAllBytes(path);
        if (HasMagic(all))
        {
            skipped++;
            Console.WriteLine("  已是密文，跳过: " + path);
            continue;
        }
        longTotal += all.Length;
        Console.WriteLine("  加密: " + path + " (" + all.Length + " 字节)");
        if (!dryRun) File.WriteAllBytes(path, EncryptFileContent(all, key));
        encrypted++;
    }

    Console.WriteLine((dryRun ? "[dry-run] 将加密 " : "已加密 ") + encrypted + " 个文件（跳过 " + skipped +
                      " 个已是密文的），明文合计 " + (longTotal / 1024 / 1024) + " MiB");
    if (dryRun) return;
    if (encrypted == 0 && skipped == 0)
        throw new InvalidOperationException("目录里没有任何匹配文件——检查 --dir 与 --pattern（加密步骤不能静默什么都不做）");
}

static int VerifyDir(string[] args, byte[]? key)
{
    var failures = 0;
    var checkedFiles = 0;
    foreach (var path in MatchedFiles(args))
    {
        checkedFiles++;
        var all = File.ReadAllBytes(path);
        if (!HasMagic(all))
        {
            Console.Error.WriteLine("明文（未加密）: " + path);
            failures++;
            continue;
        }
        if (key is null)
        {
            Console.WriteLine("密文 OK（未验密钥）: " + path);
            continue;
        }
        try
        {
            var plaintext = DecryptToPlaintext(all, key);
            var isOnnx = plaintext.Length > 4 && plaintext[0] == 0x08;
            Console.WriteLine((isOnnx ? "密文 + 解密 OK: " : "密文可解密，但内容不像 ONNX: ") +
                              path + " (" + plaintext.Length + " 字节)");
            if (!isOnnx) failures++;
        }
        catch (CryptographicException)
        {
            Console.Error.WriteLine("用给定密钥解密失败（密钥不对或文件被篡改）: " + path);
            failures++;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("校验异常 " + path + ": " + ex.Message);
            failures++;
        }
    }

    if (checkedFiles == 0)
    {
        Console.Error.WriteLine("目录里没有任何匹配文件，无法校验（检查 --dir / --pattern）");
        return 1;
    }
    Console.WriteLine(failures == 0
        ? "校验通过：" + checkedFiles + " 个文件全部是密文" + (key is null ? "（未给密钥，只查了容器头）" : "且都能用给定密钥解密")
        : "校验失败：" + failures + " / " + checkedFiles + " 个文件有问题");
    return failures == 0 ? 0 : 1;
}

static void Inspect(string[] args)
{
    var path = Require(args, "--in");
    var all = File.ReadAllBytes(path);
    if (HasMagic(all))
    {
        Console.WriteLine("BSMO 容器 v" + all[4] + "，密文 " + (all.Length - HeaderLen) + " 字节，文件 " + path);
        return;
    }
    var head = all.Length >= 4 ? BitConverter.ToString(all.AsSpan(0, 4).ToArray()) : "(空文件)";
    Console.WriteLine("明文（首 4 字节 " + head + "），文件 " + path);
}
