using MusicOrganiser.Core.Library;
using MusicOrganiser.ViewModels;
using Xunit;

namespace MusicOrganiser.Core.Tests;

/// <summary>Settings › Sound output, with pretend outputs: nothing here opens a real one.</summary>
public sealed class SoundOutputSettingTests : IDisposable
{
    readonly LibraryDb _db = new(Path.Combine(Fixtures.TempDir(), "library.db"));
    readonly Outputs _outputs = new();

    public void Dispose() => _db.Dispose();

    static string DefaultLabel(string now) =>
        OperatingSystem.IsWindows() ? $"Windows' default output (now {now})" : "The computer's default output";

    [Fact]
    public void The_saved_output_is_used_from_the_first_sound_without_opening_anything()
    {
        _db.SetSetting(SoundOutputSetting.Key, "Focusrite USB");

        var setting = new SoundOutputSetting(_db, _outputs);

        Assert.Equal(["prefer Focusrite USB"], _outputs.Calls);
        Assert.Equal("Focusrite USB", setting.Selected?.Name);
    }

    [Fact]
    public async Task Settings_lists_the_default_first_then_every_output()
    {
        var setting = new SoundOutputSetting(_db, _outputs);

        await setting.RefreshAsync();

        Assert.Equal([DefaultLabel("Speakers (HDMI)"), "Speakers (HDMI)", "Focusrite USB"], setting.Choices.Select(c => c.Label));
        Assert.Null(setting.Selected?.Name);
        Assert.Equal("Sounds play through Speakers (HDMI).", setting.Note);
        Assert.Equal(["prefer ", "list", "check"], _outputs.Calls);   // listing isn't choosing
    }

    [Fact]
    public async Task Choosing_an_output_saves_it_and_moves_sound_there()
    {
        var setting = new SoundOutputSetting(_db, _outputs);
        await setting.RefreshAsync();

        setting.Selected = setting.Choices.Single(c => c.Name == "Focusrite USB");
        await Until(() => !setting.Busy);

        Assert.Equal("Focusrite USB", _db.GetSetting(SoundOutputSetting.Key));
        Assert.Equal("choose Focusrite USB", _outputs.Calls[^1]);
        Assert.Equal("Sounds play through Focusrite USB.", setting.Note);

        setting.Selected = setting.Choices[0];
        await Until(() => !setting.Busy);

        Assert.Equal("", _db.GetSetting(SoundOutputSetting.Key));
        Assert.Equal("choose ", _outputs.Calls[^1]);
    }

    [Fact]
    public async Task A_saved_output_thats_unplugged_stays_chosen_and_says_so()
    {
        _db.SetSetting(SoundOutputSetting.Key, "Old interface");
        var setting = new SoundOutputSetting(_db, _outputs);

        await setting.RefreshAsync();

        Assert.Equal("Old interface (not there now)", setting.Selected?.Label);
        Assert.Equal("Old interface", _db.GetSetting(SoundOutputSetting.Key));
        Assert.DoesNotContain(_outputs.Calls, c => c.StartsWith("choose"));
    }

    [Fact]
    public async Task Without_a_choice_of_output_the_card_is_left_out()
    {
        var setting = new SoundOutputSetting(_db, null);

        await setting.RefreshAsync();

        Assert.False(setting.IsAvailable);
        Assert.Single(setting.Choices);
    }

    static async Task Until(Func<bool> done)
    {
        for (var waited = System.Diagnostics.Stopwatch.StartNew(); !done(); await Task.Delay(10))
            if (waited.Elapsed > TimeSpan.FromSeconds(10)) Assert.Fail("Timed out.");
    }

    /// <summary>Two outputs, the first the default, and a record of what was asked.</summary>
    sealed class Outputs : ISoundOutputs
    {
        string? _now;
        public List<string> Calls { get; } = [];

        public IReadOnlyList<string> List()
        {
            lock (Calls) Calls.Add("list");
            return ["Speakers (HDMI)", "Focusrite USB"];
        }

        public void Prefer(string? name)
        {
            lock (Calls) Calls.Add($"prefer {name}");
            _now = name;
        }

        public string Choose(string? name)
        {
            lock (Calls) Calls.Add($"choose {name}");
            _now = name;
            return $"Sounds play through {name ?? "Speakers (HDMI)"}.";
        }

        public string Check()
        {
            lock (Calls) Calls.Add("check");
            return $"Sounds play through {_now ?? "Speakers (HDMI)"}.";
        }
    }
}
