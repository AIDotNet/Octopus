using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.Mobile.Conversations;
using Octopus.Mobile.Remote;
using Octopus.Mobile.Voice;
using Octopus.Remote.Relay;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// Voice input (design/ui/移动端输入框重写与语音输入方案.html, scheme B): the wire formats of the three cloud engines,
/// the settings, "hold to talk" in the composer, and the page's microphone switch.
/// </summary>
public class VoiceTests
{
    // ---- fakes ----

    private sealed class FakeSession : IDictationSession
    {
        public string Final { get; set; } = "帮我看看 SendCommand";
        public bool Stopped { get; private set; }
        public bool Cancelled { get; private set; }

        public event Action<float>? LevelChanged;
        public event Action<string, string>? TextChanged;
        public event Action<DictationException>? Failed;

        public void Speak(string settled, string tail, float level = 0.5f)
        {
            LevelChanged?.Invoke(level);
            TextChanged?.Invoke(settled, tail);
        }

        public void Fail(DictationException e) => Failed?.Invoke(e);

        public Task<string> StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return Task.FromResult(Final);
        }

        public void Cancel() => Cancelled = true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeEngine : IDictationEngine
    {
        public DictationProblem Problem { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public DictationException? Refusal { get; set; }
        public FakeSession? Last { get; private set; }

        public DictationProblem Check(VoiceSettings settings) => Problem;

        public async Task<IDictationSession> StartAsync(VoiceSettings settings, CancellationToken cancellationToken)
        {
            if (Gate is { } gate) await gate.Task;
            if (Refusal is { } refusal) throw refusal;
            Last = new FakeSession();
            return Last;
        }
    }

    private sealed class Composer
    {
        public Composer(FakeEngine engine, VoiceEngineKind kind = VoiceEngineKind.System)
        {
            Engine = engine;
            Service = new VoiceService(new Dictionary<VoiceEngineKind, IDictationEngine> { [kind] = engine }, new VoiceSettings { Engine = kind });
            ViewModel = new VoiceComposerViewModel(Service, Filled.Add, text =>
            {
                Sent.Add(text);
                return Task.CompletedTask;
            }, () => SettingsOpened++, Notices.Add);
        }

        public FakeEngine Engine { get; }
        public VoiceService Service { get; }
        public VoiceComposerViewModel ViewModel { get; }
        public List<string> Filled { get; } = [];
        public List<string> Sent { get; } = [];
        public List<string> Notices { get; } = [];
        public int SettingsOpened { get; private set; }
    }

    // ---- hold to talk ----

    [AvaloniaFact]
    public void Holding_shows_live_text_and_releasing_fills_the_prompt_field()
    {
        var composer = new Composer(new FakeEngine());
        var vm = composer.ViewModel;
        vm.ToggleVoiceModeCommand.Execute(null);
        Assert.True(vm.IsVoiceMode);

        vm.Press();
        Assert.True(Phone.Until(() => composer.Engine.Last is not null));
        Assert.True(vm.IsRecording);
        Assert.Equal("松开 结束", vm.HoldText);
        Assert.Equal("松开 填入输入框", vm.ReleaseText);

        composer.Engine.Last!.Speak("帮我看看", " SendComm", 0.8f);
        Assert.True(Phone.Until(() => vm.TailText == " SendComm"));
        Assert.Equal("帮我看看", vm.SettledText);
        Assert.Equal(0.8, vm.Level, 3);
        Assert.Equal("", vm.Placeholder);

        vm.Release();
        Assert.True(Phone.Until(() => composer.Filled.Count == 1));
        Assert.Equal("帮我看看 SendCommand", composer.Filled[0]);
        Assert.True(composer.Engine.Last.Stopped);
        Assert.False(vm.IsRecording);
        Assert.Empty(composer.Sent);
        // Back to the keyboard so the text can be checked before it goes.
        Assert.False(vm.IsVoiceMode);
    }

    [AvaloniaFact]
    public void Dragging_onto_cancel_throws_the_dictation_away()
    {
        var composer = new Composer(new FakeEngine());
        var vm = composer.ViewModel;
        vm.Press();
        Assert.True(Phone.Until(() => composer.Engine.Last is not null));

        vm.Move(VoiceZone.Cancel);
        Assert.True(vm.IsCancelZone);
        Assert.Equal("松开 取消", vm.ReleaseText);
        vm.Release();

        Assert.True(Phone.Until(() => !vm.IsRecording));
        Assert.True(composer.Engine.Last!.Cancelled);
        Assert.False(composer.Engine.Last.Stopped);
        Assert.Empty(composer.Filled);
        Assert.Empty(composer.Sent);
    }

    [AvaloniaFact]
    public void Dragging_onto_send_sends_the_text_straight_away()
    {
        var composer = new Composer(new FakeEngine());
        var vm = composer.ViewModel;
        vm.Press();
        Assert.True(Phone.Until(() => composer.Engine.Last is not null));

        vm.Move(VoiceZone.Send);
        Assert.Equal("松开 直接发送", vm.ReleaseText);
        // Back over the bar: the choice follows the finger.
        vm.Move(VoiceZone.Fill);
        Assert.Equal("松开 填入输入框", vm.ReleaseText);
        vm.Move(VoiceZone.Send);
        vm.Release();

        Assert.True(Phone.Until(() => composer.Sent.Count == 1));
        Assert.Equal("帮我看看 SendCommand", composer.Sent[0]);
        Assert.Empty(composer.Filled);
    }

    [AvaloniaFact]
    public void Releasing_while_the_engine_is_still_starting_finishes_once_it_is_there()
    {
        var engine = new FakeEngine { Gate = new TaskCompletionSource() };
        var composer = new Composer(engine);
        var vm = composer.ViewModel;
        vm.Press();
        Assert.True(vm.IsRecording);
        vm.Release();
        Assert.Null(engine.Last);

        engine.Gate.SetResult();
        Assert.True(Phone.Until(() => composer.Filled.Count == 1));
        Assert.True(engine.Last!.Stopped);
    }

    [AvaloniaFact]
    public void A_cloud_engine_without_its_key_says_so_and_opens_the_settings()
    {
        var composer = new Composer(new FakeEngine { Problem = DictationProblem.NotConfigured }, VoiceEngineKind.Aliyun);
        var vm = composer.ViewModel;
        vm.Press();

        Assert.False(vm.IsRecording);
        Assert.Null(composer.Engine.Last);
        Assert.True(vm.HasProblem);
        Assert.Contains("阿里云百炼 Fun-ASR", vm.ProblemText);

        vm.OpenSettingsCommand.Execute(null);
        Assert.Equal(1, composer.SettingsOpened);
        Assert.False(vm.HasProblem);
    }

    [AvaloniaFact]
    public void A_refused_microphone_and_a_dropped_connection_end_the_dictation_with_a_reason()
    {
        var engine = new FakeEngine { Refusal = new DictationException(DictationErrorKind.PermissionDenied, "RECORD_AUDIO") };
        var composer = new Composer(engine);
        var vm = composer.ViewModel;
        vm.Press();
        Assert.True(Phone.Until(() => !vm.IsRecording));
        Assert.Contains("麦克风", vm.ProblemText);

        engine.Refusal = null;
        vm.Press();
        Assert.True(Phone.Until(() => engine.Last is not null && vm.IsRecording));
        engine.Last!.Fail(new DictationException(DictationErrorKind.Network, "reset by peer"));
        Assert.True(Phone.Until(() => !vm.IsRecording));
        Assert.Contains(composer.Notices, notice => notice.Contains("reset by peer", StringComparison.Ordinal));
        Assert.True(engine.Last.Cancelled);
        Assert.Empty(composer.Filled);
    }

    [AvaloniaFact]
    public void Aborting_drops_the_dictation_and_late_text_is_ignored()
    {
        var composer = new Composer(new FakeEngine());
        var vm = composer.ViewModel;
        vm.Press();
        Assert.True(Phone.Until(() => composer.Engine.Last is not null));
        var session = composer.Engine.Last!;

        vm.Abort();
        session.Speak("迟到的", "文字");
        Phone.Pump();

        Assert.False(vm.IsRecording);
        Assert.True(session.Cancelled);
        Assert.Equal("", vm.SettledText);
        Assert.Empty(composer.Filled);
    }

    [Fact]
    public void The_finger_is_over_cancel_or_send_only_once_it_rose_above_the_bar()
    {
        var origin = new Point(200, 800);
        Assert.Equal(VoiceZone.Fill, ConversationPage.ZoneAt(new Point(40, 760), origin, 390));
        Assert.Equal(VoiceZone.Cancel, ConversationPage.ZoneAt(new Point(80, 690), origin, 390));
        Assert.Equal(VoiceZone.Send, ConversationPage.ZoneAt(new Point(300, 690), origin, 390));
    }

    // ---- the conversation page ----

    [AvaloniaFact]
    public void The_microphone_switches_the_prompt_field_for_hold_to_talk_and_dictation_lands_at_the_caret()
    {
        var previous = MobileHost.Voice;
        MobileHost.Voice = new VoiceService(new Dictionary<VoiceEngineKind, IDictationEngine> { [VoiceEngineKind.System] = new FakeEngine() });
        try
        {
            using var phone = new Phone();
            phone.Workspace.ConversationStep = TimeSpan.FromMilliseconds(40);
            var station = phone.Workspace.Hosts.Single(host => host.Id == "station");
            phone.Shell.ShowHost(station);
            phone.Shell.ShowSession(station.Projects[0].Sessions.First(session => session.IsGui));
            Assert.True(Phone.Until(() => phone.Top<ConversationPage>().DataContext is ConversationViewModel { IsLoaded: true }));
            var page = phone.Top<ConversationPage>();
            var vm = (ConversationViewModel)page.DataContext!;

            var voiceButton = page.FindControl<Control>("VoiceModeButton")!;
            var prompt = page.FindControl<Control>("PromptField")!;
            var bar = page.FindControl<Control>("HoldToTalkBar")!;
            Assert.True(voiceButton.IsEffectivelyVisible);
            Assert.True(prompt.IsVisible);
            Assert.False(bar.IsVisible);

            vm.Voice.ToggleVoiceModeCommand.Execute(null);
            Phone.Pump();
            Assert.False(prompt.IsVisible);
            Assert.True(bar.IsVisible);
            Assert.True(page.FindControl<Control>("KeyboardModeButton")!.IsVisible);
            Assert.False(page.FindControl<Control>("SendButton")!.IsEffectivelyVisible);

            // Latin words get a space before them; Chinese runs straight on.
            vm.PromptText = "fix please";
            vm.PromptCaretIndex = 3;
            vm.InsertDictation("the bug");
            Assert.Equal("fix the bug please", vm.PromptText);
            Assert.Equal(11, vm.PromptCaretIndex);
            vm.PromptText = "修复";
            vm.PromptCaretIndex = 2;
            vm.InsertDictation("发送按钮");
            Assert.Equal("修复发送按钮", vm.PromptText);
        }
        finally
        {
            MobileHost.Voice = previous;
        }
    }

    [AvaloniaFact]
    public void Without_any_engine_the_microphone_stays_hidden()
    {
        using var phone = new Phone();
        var station = phone.Workspace.Hosts.Single(host => host.Id == "station");
        phone.Shell.ShowHost(station);
        phone.Shell.ShowSession(station.Projects[0].Sessions.First(session => session.IsGui));
        Assert.True(Phone.Until(() => phone.Top<ConversationPage>().DataContext is ConversationViewModel { IsLoaded: true }));
        Assert.False(phone.Top<ConversationPage>().FindControl<Control>("VoiceModeButton")!.IsEffectivelyVisible);
    }

    // ---- settings ----

    [Fact]
    public async Task Settings_keep_the_keys_as_secrets_and_the_rest_in_a_file()
    {
        var directory = Directory.CreateTempSubdirectory("octopus-voice-").FullName;
        try
        {
            var store = new FileMobileStore(directory);
            var service = new VoiceService(store);
            await service.Loaded;
            Assert.Equal(VoiceEngineKind.System, service.Settings.Engine);

            var settings = service.Settings.Clone();
            settings.Engine = VoiceEngineKind.Volcengine;
            settings.VolcengineAccessToken = "volc-secret";
            settings.OpenAiApiKey = "sk-secret";
            settings.Language = VoiceLanguage.Chinese;
            await service.SaveAsync(settings);

            var file = await File.ReadAllTextAsync(Path.Combine(directory, "voice.json"), TestContext.Current.CancellationToken);
            Assert.Contains("\"Volcengine\"", file, StringComparison.Ordinal);
            Assert.DoesNotContain("secret", file, StringComparison.Ordinal);

            var again = new VoiceService(store);
            await again.Loaded;
            Assert.Equal(VoiceEngineKind.Volcengine, again.Settings.Engine);
            Assert.Equal(VoiceLanguage.Chinese, again.Settings.Language);
            Assert.Equal("volc-secret", again.Settings.VolcengineAccessToken);
            Assert.Equal("sk-secret", again.Settings.OpenAiApiKey);
            Assert.Equal("", again.Settings.AliyunApiKey);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Without_a_head_nothing_is_supported_and_the_cloud_engines_need_a_microphone()
    {
        var service = new VoiceService();
        Assert.False(service.IsSupported);
        Assert.Equal(DictationProblem.NoSystemRecognizer, service.Check(VoiceEngineKind.System));
        Assert.Equal(DictationProblem.NoMicrophone, service.Check(VoiceEngineKind.OpenAi));
    }

    // ---- wire formats ----

    [Fact]
    public void Level_and_wav_header()
    {
        Assert.Equal(0, PcmLevel.Of(new byte[640]));
        var loud = new byte[640];
        for (var i = 0; i < 320; i++) BinaryPrimitives.WriteInt16LittleEndian(loud.AsSpan(2 * i), (short)(i % 2 == 0 ? 30000 : -30000));
        Assert.True(PcmLevel.Of(loud) > 0.9f);

        var wav = CloudWire.Wav(new byte[3200]);
        Assert.Equal(44 + 3200, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(3200, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal("héllo"u8.ToArray(), CloudWire.Gunzip(CloudWire.Gzip("héllo"u8)));
    }

    [Fact]
    public void Aliyun_run_task_endpoint_and_sentences()
    {
        var settings = new VoiceSettings { AliyunApiKey = "sk", Language = VoiceLanguage.Chinese };
        using (var runTask = JsonDocument.Parse(AliyunProtocol.RunTask("t1", settings)))
        {
            var root = runTask.RootElement;
            Assert.Equal("run-task", root.GetProperty("header").GetProperty("action").GetString());
            Assert.Equal("duplex", root.GetProperty("header").GetProperty("streaming").GetString());
            var payload = root.GetProperty("payload");
            Assert.Equal("fun-asr-realtime", payload.GetProperty("model").GetString());
            Assert.Equal("recognition", payload.GetProperty("function").GetString());
            Assert.Equal("pcm", payload.GetProperty("parameters").GetProperty("format").GetString());
            Assert.Equal(16000, payload.GetProperty("parameters").GetProperty("sample_rate").GetInt32());
            Assert.Equal("zh", payload.GetProperty("parameters").GetProperty("language_hints")[0].GetString());
        }
        using (var finish = JsonDocument.Parse(AliyunProtocol.FinishTask("t1")))
        {
            Assert.Equal("finish-task", finish.RootElement.GetProperty("header").GetProperty("action").GetString());
            Assert.Equal("t1", finish.RootElement.GetProperty("header").GetProperty("task_id").GetString());
        }

        Assert.Equal("wss://dashscope.aliyuncs.com/api-ws/v1/inference", AliyunProtocol.EndpointOf(settings).ToString());
        settings.AliyunWorkspaceId = "ws1";
        settings.AliyunRegion = AliyunRegion.International;
        Assert.Equal("wss://ws1.ap-southeast-1.maas.aliyuncs.com/api-ws/v1/inference", AliyunProtocol.EndpointOf(settings).ToString());

        var transcript = new AliyunTranscript();
        Assert.True(transcript.Apply(AliyunProtocol.Parse(Result(1, "帮我", false))));
        Assert.Equal(("", "帮我"), (transcript.Settled, transcript.Tail));
        Assert.True(transcript.Apply(AliyunProtocol.Parse(Result(1, "帮我看看。", true))));
        Assert.True(transcript.Apply(AliyunProtocol.Parse(Result(2, "Send", false))));
        Assert.Equal(("帮我看看。", "Send"), (transcript.Settled, transcript.Tail));
        Assert.False(transcript.Apply(AliyunProtocol.Parse(Result(0, "", false, heartbeat: true))));
        Assert.Equal("帮我看看。Send", transcript.Text);

        var failed = AliyunProtocol.Parse("""{"header":{"task_id":"t1","event":"task-failed","error_code":"CLIENT_ERROR","error_message":"request timeout after 23 seconds."},"payload":{}}"""u8);
        Assert.Equal("task-failed", failed.Event);
        Assert.Equal("CLIENT_ERROR", failed.ErrorCode);

        static byte[] Result(int id, string text, bool end, bool heartbeat = false) =>
            JsonSerializer.SerializeToUtf8Bytes(new System.Text.Json.Nodes.JsonObject
            {
                ["header"] = new System.Text.Json.Nodes.JsonObject { ["task_id"] = "t1", ["event"] = "result-generated" },
                ["payload"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["output"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["sentence"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["sentence_id"] = id,
                            ["text"] = text,
                            ["sentence_end"] = end,
                            ["heartbeat"] = heartbeat,
                        },
                    },
                },
            });
    }

    [Fact]
    public void Volcengine_frames_both_ways_and_both_consoles()
    {
        var settings = new VoiceSettings { VolcengineAccessToken = "token", Punctuation = false };
        var request = VolcengineProtocol.FullRequest(settings);
        Assert.Equal(new byte[] { 0x11, 0x10, 0x11, 0x00 }, request[..4]);
        Assert.Equal(request.Length - 8, (int)BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4)));
        using (var json = JsonDocument.Parse(CloudWire.Gunzip(request.AsSpan(8))))
        {
            var root = json.RootElement;
            Assert.Equal(16000, root.GetProperty("audio").GetProperty("rate").GetInt32());
            Assert.Equal("bigmodel", root.GetProperty("request").GetProperty("model_name").GetString());
            Assert.False(root.GetProperty("request").GetProperty("enable_punc").GetBoolean());
        }
        Assert.Equal(new byte[] { 0x11, 0x20, 0x01, 0x00 }, VolcengineProtocol.Audio(new byte[10], last: false)[..4]);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x01, 0x00 }, VolcengineProtocol.Audio([], last: true)[..4]);

        var payload = CloudWire.Gzip("""{"result":{"text":"你好世界","utterances":[{"text":"你好","definite":true},{"text":"世界","definite":false}]}}"""u8);
        var response = new byte[12 + payload.Length];
        new byte[] { 0x11, 0x93, 0x11, 0x00 }.CopyTo(response, 0);
        BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(4), -3);
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(8), (uint)payload.Length);
        payload.CopyTo(response, 12);
        var frame = VolcengineProtocol.Parse(response);
        Assert.True(frame.IsLast);
        Assert.Equal(new VolcengineResult("你好世界", "你好", "世界"), frame.Result);

        var message = "empty audio"u8.ToArray();
        var error = new byte[12 + message.Length];
        new byte[] { 0x11, 0xF0, 0x10, 0x00 }.CopyTo(error, 0);
        BinaryPrimitives.WriteUInt32BigEndian(error.AsSpan(4), 45000002);
        BinaryPrimitives.WriteUInt32BigEndian(error.AsSpan(8), (uint)message.Length);
        message.CopyTo(error, 12);
        var failed = VolcengineProtocol.Parse(error);
        Assert.Equal(45000002u, failed.ErrorCode);
        Assert.Equal("empty audio", failed.ErrorMessage);
        Assert.Equal(DictationErrorKind.NoSpeech, VolcengineProtocol.KindOf(45000002));

        var newer = VolcengineProtocol.HeadersOf(settings, "c1").ToDictionary(header => header.Name, header => header.Value);
        Assert.Equal("token", newer["X-Api-Key"]);
        Assert.False(newer.ContainsKey("X-Api-App-Key"));
        Assert.Equal("volc.bigasr.sauc.duration", newer["X-Api-Resource-Id"]);
        settings.VolcengineAppId = "app";
        var older = VolcengineProtocol.HeadersOf(settings, "c1").ToDictionary(header => header.Name, header => header.Value);
        Assert.Equal("app", older["X-Api-App-Key"]);
        Assert.Equal("token", older["X-Api-Access-Key"]);
        Assert.False(older.ContainsKey("X-Api-Key"));
    }

    [Fact]
    public async Task OpenAi_form_stream_events_and_errors()
    {
        var settings = new VoiceSettings { OpenAiApiKey = "sk", OpenAiBaseUrl = "https://example.com/v1/", Language = VoiceLanguage.English };
        Assert.Equal("https://example.com/v1/audio/transcriptions", OpenAiProtocol.EndpointOf(settings).ToString());

        var form = await FormText(settings);
        Assert.Contains("name=model", form, StringComparison.Ordinal);
        Assert.Contains("gpt-transcribe", form, StringComparison.Ordinal);
        Assert.Contains("name=\"languages[]\"", form, StringComparison.Ordinal);
        Assert.Contains("filename=speech.wav", form, StringComparison.Ordinal);
        settings.OpenAiModel = "whisper-1";
        Assert.Contains("name=language", await FormText(settings), StringComparison.Ordinal);

        Assert.Equal(new OpenAiEvent("transcript.text.delta", " see"), OpenAiProtocol.ParseEvent("""data: {"type":"transcript.text.delta","delta":" see"}"""));
        Assert.Equal(new OpenAiEvent("transcript.text.done", "I see skies"), OpenAiProtocol.ParseEvent("""data: {"type":"transcript.text.done","text":"I see skies"}"""));
        Assert.Equal(default, OpenAiProtocol.ParseEvent(": keep-alive"));
        Assert.Equal("hello", OpenAiProtocol.TextOf("""{"text":"hello"}"""));
        Assert.Equal("Incorrect API key provided", OpenAiProtocol.ErrorOf("""{"error":{"message":"Incorrect API key provided","type":"invalid_request_error"}}"""));

        static async Task<string> FormText(VoiceSettings settings)
        {
            using var form = OpenAiProtocol.Form(settings, CloudWire.Wav(new byte[320]));
            return await form.ReadAsStringAsync(TestContext.Current.CancellationToken);
        }
    }

    // ---- hotwords ----

    [Fact]
    public void Hotwords_are_the_code_like_words_of_the_conversation()
    {
        var words = VoiceHotwords.Collect(
            ["Octopus", "接口文档整理"],
            ["src/Octopus.Mobile/Conversations/ConversationViewModel.cs", "README.md", null],
            ["帮我把 SendCommand 拆成两步，先校验 PromptText，再调用 send_prompt_async 和 v2 接口", "The quick brown fox"]);

        Assert.Equal(["Octopus", "接口文档整理", "ConversationViewModel.cs", "ConversationViewModel", "README.md", "README", "SendCommand", "PromptText", "send_prompt_async"], words);
        Assert.DoesNotContain("quick", words);
        Assert.True(VoiceHotwords.IsCodeLike("gpt4o"));
        Assert.False(VoiceHotwords.IsCodeLike("Hello"));
        Assert.Equal("SendCommand PromptText", VoiceHotwords.Joined(["SendCommand", "PromptText", "ConversationViewModel"], 25));

        var many = VoiceHotwords.Collect(Enumerable.Range(0, 100).Select(n => $"Name{n}"), [], []);
        Assert.Equal(VoiceHotwords.Max, many.Count);
    }

    [Fact]
    public async Task Hotwords_reach_each_cloud_engine_in_its_own_form()
    {
        var settings = new VoiceSettings { Hotwords = ["SendCommand", "ConversationViewModel"], Language = VoiceLanguage.Chinese };

        using (var runTask = JsonDocument.Parse(AliyunProtocol.RunTask("t1", settings)))
        {
            var context = runTask.RootElement.GetProperty("payload").GetProperty("input").GetProperty("context");
            Assert.Equal("SendCommand ConversationViewModel", context[0].GetProperty("content")[0].GetProperty("text").GetString());
        }
        settings.AliyunModel = "paraformer-realtime-v2";
        using (var paraformer = JsonDocument.Parse(AliyunProtocol.RunTask("t1", settings)))
        {
            Assert.False(paraformer.RootElement.GetProperty("payload").GetProperty("input").TryGetProperty("context", out _));
        }

        var request = VolcengineProtocol.FullRequest(settings);
        using (var json = JsonDocument.Parse(CloudWire.Gunzip(request.AsSpan(8))))
        {
            var context = json.RootElement.GetProperty("request").GetProperty("corpus").GetProperty("context").GetString()!;
            using var hotwords = JsonDocument.Parse(context);
            Assert.Equal("ConversationViewModel", hotwords.RootElement.GetProperty("hotwords")[1].GetProperty("word").GetString());
        }

        settings.Hotwords = ["SendCommand", "bad<word>"];
        using var form = OpenAiProtocol.Form(settings, CloudWire.Wav(new byte[320]));
        var text = await form.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("SendCommand", text, StringComparison.Ordinal);
        Assert.DoesNotContain("bad<word>", text, StringComparison.Ordinal);
        settings.OpenAiModel = "whisper-1";
        using var older = OpenAiProtocol.Form(settings, CloudWire.Wav(new byte[320]));
        Assert.Contains("name=prompt", await older.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void A_conversation_offers_its_project_and_code_words_as_hotwords()
    {
        using var phone = new Phone();
        var station = phone.Workspace.Hosts.Single(host => host.Id == "station");
        phone.Shell.ShowHost(station);
        phone.Shell.ShowSession(station.Projects[0].Sessions.First(session => session.IsGui));
        Assert.True(Phone.Until(() => phone.Top<ConversationPage>().DataContext is ConversationViewModel { IsLoaded: true }));
        var vm = (ConversationViewModel)phone.Top<ConversationPage>().DataContext!;

        var words = vm.CollectHotwords();
        Assert.Equal(station.Projects[0].Name, words[0]);
        Assert.InRange(words.Count, 1, VoiceHotwords.Max);
    }

    // ---- Octopus cloud recognition ----

    /// <summary>A microphone that "hears" a few chunks as soon as it starts.</summary>
    private sealed class FakeCapture : IAudioCapture
    {
        public event Action<byte[]>? Captured;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            for (var i = 0; i < 3; i++) Captured?.Invoke(new byte[3200]);
            return Task.CompletedTask;
        }

        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The server's side of /v1/voice/stream, on a loopback HttpListener.</summary>
    private sealed class FakeVoiceServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;

        public FakeVoiceServer(Func<HttpListenerContext, Task> handle)
        {
            var port = FreePort();
            Url = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(Url.ToString());
            _listener.Start();
            _serving = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                    _ = handle(context);
                }
            });
        }

        public Uri Url { get; }
        public List<string> Tokens { get; } = [];

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            await _serving;
        }

        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
    }

    [Fact]
    public async Task Octopus_cloud_recognition_streams_through_the_account_server_and_renews_a_refused_token()
    {
        JsonDocument? start = null;
        long audio = 0;
        FakeVoiceServer? server = null;
        await using var fake = server = new FakeVoiceServer(async context =>
        {
            var token = context.Request.Headers["Authorization"] ?? "";
            server!.Tokens.Add(token);
            if (context.Request.Url!.AbsolutePath != "/v1/voice/stream" || token != "Bearer fresh")
            {
                context.Response.StatusCode = 401;
                context.Response.Close();
                return;
            }
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var (type, data) = await CloudWire.ReceiveAsync(socket, CancellationToken.None);
            Assert.Equal(WebSocketMessageType.Text, type);
            start = JsonDocument.Parse(data);
            while (true)
            {
                (type, data) = await CloudWire.ReceiveAsync(socket, CancellationToken.None);
                if (type == WebSocketMessageType.Binary)
                {
                    audio += data.Length;
                    continue;
                }
                break;
            }
            await socket.SendAsync("""{"type":"text","settled":"帮我","tail":"看看"}"""u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.SendAsync("""{"type":"done","text":"帮我看看 SendCommand"}"""u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });

        var engine = new OctopusDictationEngine(() => new FakeCapture(),
            (fresh, _) => Task.FromResult<RelayEndpoint?>(new RelayEndpoint(fake.Url, fresh ? "fresh" : "stale")),
            () => true);
        var settings = new VoiceSettings { Engine = VoiceEngineKind.Octopus, Language = VoiceLanguage.Chinese, Hotwords = ["SendCommand"] };
        var texts = new List<string>();
        await using var session = await engine.StartAsync(settings, TestContext.Current.CancellationToken);
        session.TextChanged += (settled, tail) => texts.Add(settled + tail);
        var final = await session.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal("帮我看看 SendCommand", final);
        Assert.Contains("帮我看看", texts);
        Assert.Equal(["Bearer stale", "Bearer fresh"], fake.Tokens);
        Assert.Equal("start", start!.RootElement.GetProperty("type").GetString());
        Assert.Equal("zh", start.RootElement.GetProperty("language").GetString());
        Assert.Equal("SendCommand", start.RootElement.GetProperty("hotwords")[0].GetString());
        Assert.Equal(9600, audio);
        start.Dispose();
    }

    [Fact]
    public async Task Octopus_cloud_recognition_says_why_the_server_refused()
    {
        await using var fake = new FakeVoiceServer(context =>
        {
            context.Response.StatusCode = context.Request.Url!.AbsolutePath.EndsWith("/stream", StringComparison.Ordinal) ? 429 : 404;
            context.Response.Close();
            return Task.CompletedTask;
        });
        var engine = new OctopusDictationEngine(() => new FakeCapture(),
            (_, _) => Task.FromResult<RelayEndpoint?>(new RelayEndpoint(fake.Url, "token")), () => true);

        await using var session = await engine.StartAsync(new VoiceSettings(), TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<DictationException>(() => session.StopAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DictationErrorKind.Quota, failure.Kind);

        var signedOut = new OctopusDictationEngine(() => new FakeCapture(), (_, _) => Task.FromResult<RelayEndpoint?>(null), () => false);
        Assert.Equal(DictationProblem.NotSignedIn, signedOut.Check(new VoiceSettings()));

        Assert.Equal(new Uri("wss://octoterm.si/base/v1/voice/stream"), OctopusVoiceProtocol.StreamUri(new Uri("https://octoterm.si/base/")));
        Assert.Equal(DictationErrorKind.ServerOff, OctopusVoiceProtocol.KindOf(HttpStatusCode.NotFound));
        Assert.Equal(new OctopusVoiceStatus(true, "aliyun", 1800, 60),
            OctopusVoiceProtocol.ParseStatus("""{"available":true,"provider":"aliyun","dailySeconds":1800,"usedSeconds":60,"maxSeconds":90}"""));
    }
}
