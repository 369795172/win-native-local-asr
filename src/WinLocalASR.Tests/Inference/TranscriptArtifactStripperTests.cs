using WinLocalASR.Core.Inference;
using Xunit;

namespace WinLocalASR.Tests.Inference;

/// <summary>
/// Artifact-strip fixtures are the verbatim samples pinned in docs/rfc.md §Inference Contract.
/// </summary>
public class TranscriptArtifactStripperTests
{
    [Fact]
    public void Strips_english_artifact_prefix_verbatim()
    {
        Assert.Equal(
            "The weather is sunny today.",
            TranscriptArtifactStripper.Strip("language English<asr_text>The weather is sunny today."));
    }

    [Fact]
    public void Strips_chinese_artifact_prefix_verbatim()
    {
        Assert.Equal(
            "今天的天气晴朗，气温二十六度，适合外出散步。",
            TranscriptArtifactStripper.Strip("language Chinese<asr_text>今天的天气晴朗，气温二十六度，适合外出散步。"));
    }

    [Fact]
    public void Clean_text_is_returned_unchanged()
    {
        Assert.Equal(
            "The weather is sunny today.",
            TranscriptArtifactStripper.Strip("The weather is sunny today."));
    }

    [Fact]
    public void Prefix_only_yields_empty_string()
    {
        Assert.Equal(string.Empty, TranscriptArtifactStripper.Strip("language English<asr_text>"));
    }

    [Fact]
    public void Regex_tolerates_extra_whitespace_in_language_name()
    {
        Assert.Equal("hello", TranscriptArtifactStripper.Strip("language   English (United States)<asr_text>hello"));
    }
}
