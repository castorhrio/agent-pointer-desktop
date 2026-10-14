using System.Text;

namespace AgentPointer.Core.Rendering;

/// <summary>
/// 朗读文本的纯函数处理：剥掉 <c>{{value}}</c> 的花括号、判断文本里有没有 CJK 字符。
/// 特意放在 Core 且不依赖任何语音 API——这两条规则必须能被单测锁住。
/// </summary>
public static class SpeechText
{
    /// <summary>
    /// 把牌子上的文字转成"念出来"的样子：<c>{{482913}}</c> 只念 <c>482913</c>，
    /// 也就是把花括号全部去掉（它们只是复制按钮的记号，不是要读的字符）。
    /// 空白或只有花括号时返回空串，调用方据此不朗读。
    /// </summary>
    public static string ToSpeakable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // 与牌子显示共用同一条规则，免得念出来的和看到的对不上
        return SignText.PlainText(text).Trim();
    }

    /// <summary>
    /// 是否含 CJK 字符（汉字、假名、CJK 标点、全角形式）。
    /// 用来决定优先选中文语音还是英文语音；只覆盖 BMP，扩展 B 区（代理对）不参与判断。
    /// </summary>
    public static bool ContainsCjk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var c in text)
        {
            if (IsCjk(c)) return true;
        }

        return false;
    }

    private static bool IsCjk(char c) => c switch
    {
        >= '\u2e80' and <= '\u2eff' => true, // CJK 部首补充
        >= '\u3000' and <= '\u303f' => true, // CJK 标点
        >= '\u3040' and <= '\u30ff' => true, // 平假名 / 片假名
        >= '\u3400' and <= '\u4dbf' => true, // 汉字扩展 A
        >= '\u4e00' and <= '\u9fff' => true, // 汉字基本区
        >= '\uf900' and <= '\ufaff' => true, // 兼容汉字
        >= '\uff00' and <= '\uffef' => true, // 全角形式
        _ => false,
    };
}
