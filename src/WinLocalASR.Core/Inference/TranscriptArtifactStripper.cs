using System.Text.RegularExpressions;

namespace WinLocalASR.Core.Inference;

/// <summary>
/// Strips the spike-verified llama-server Qwen3-ASR artifact prefix from raw transcript text.
/// docs/rfc.md §Inference Contract: the `.text` field carries `language &lt;Lang&gt;&lt;asr_text&gt;`
/// before the real transcript; when the pattern is absent the raw text is returned unchanged —
/// never empty out unmatched text.
/// </summary>
public static class TranscriptArtifactStripper
{
    private static readonly Regex ArtifactPrefix =
        new("^language\\s+[^<]*<asr_text>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Strip(string text)
    {
        Match match = ArtifactPrefix.Match(text);
        return match.Success ? text[(match.Index + match.Length)..] : text;
    }
}
