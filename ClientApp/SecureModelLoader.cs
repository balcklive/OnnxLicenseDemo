// 客户端加载加密模型：从 BSMO 容器解密出模型字节，直接在内存里构造 InferenceSession，
// 全程不把明文模型写回磁盘。
//
// 容器格式（由 ModelProtector 写入，说明也在那边）：magic "BSMO" + 版本 + nonce + tag + 密文。
// 读侧同时兼容 2026-09-24 之前的旧格式（无 magic：[12 nonce][16 tag][密文]）。
//
// 老实说明局限性：解密后的明文字节短暂存在于进程内存中，构造完 Session 后我们会尽快清零
// 这块内存，但这只是"降低被内存 dump 工具捕获的概率/窗口"，不能做到绝对无法被有经验的
// 攻击者提取。真正要做到模型完全不落地到客户端，只有把推理放在你自己的服务器上、
// 客户端只传输输入输出这一条路。

using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;

namespace ClientApp;

public static class SecureModelLoader
{
    private const string Magic = "BSMO";
    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const int HeaderLen = 36;
    private const int LegacyHeaderLen = 28;

    public static InferenceSession LoadSession(string encryptedModelPath, byte[] modelKey)
    {
        var all = File.ReadAllBytes(encryptedModelPath);
        var (nonce, tag, cipher) = Unwrap(all, encryptedModelPath);

        var plaintext = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(modelKey, TagLen);
            aes.Decrypt(nonce, cipher, tag, plaintext);
            // 密钥不对，或文件被篡改，上面这行会抛 CryptographicException

            return new InferenceSession(plaintext);
        }
        finally
        {
            Array.Clear(plaintext, 0, plaintext.Length); // 尽快清零明文缓冲区
        }
    }

    /// <summary>
    /// 拆容器头。给 bot-cs/ModelVault 用的判断也放在这里：有 BSMO 头就是本工具的新格式，
    /// 没有则按旧格式（28 字节裸头）解释。
    /// </summary>
    private static (byte[] Nonce, byte[] Tag, byte[] Cipher) Unwrap(byte[] all, string path)
    {
        if (all.Length >= HeaderLen && Encoding.ASCII.GetString(all, 0, Magic.Length) == Magic)
        {
            if (all[4] != 1)
                throw new InvalidDataException("不支持的模型容器版本 " + all[4] + ": " + path);
            return (all.AsSpan(8, NonceLen).ToArray(), all.AsSpan(20, TagLen).ToArray(), all.AsSpan(HeaderLen).ToArray());
        }
        if (all.Length > LegacyHeaderLen)
            return (all.AsSpan(0, NonceLen).ToArray(), all.AsSpan(NonceLen, TagLen).ToArray(), all.AsSpan(LegacyHeaderLen).ToArray());
        throw new InvalidDataException("模型文件太小，不是加密容器: " + path);
    }
}
