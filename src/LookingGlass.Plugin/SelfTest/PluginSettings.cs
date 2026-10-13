using System.Text.Json;
using LookingGlass.Core.Client;
using LookingGlass.Core.SelfTest;

namespace LookingGlass.Plugin.SelfTest;

/// <summary>
/// The settings the self-test may change (<see cref="SelfTestSetting"/>), read and written on the plugin's settings for one
/// character and server, as JSON values. With a session manager, the ones it owns go through it (so the chat history opens
/// or closes, and its caches follow); without one (the plugin starting, putting back what an interrupted run changed),
/// straight into the settings. Each write saves the settings. Framework thread (or the plugin's constructor).
/// </summary>
internal sealed class PluginSettings(Configuration config, ulong contentId, string serverUrl, SessionManager? sessions = null, long otherUserId = 0) : ISelfTestSettings {
    public JsonElement Read(SelfTestSetting setting) => setting switch {
        SelfTestSetting.WindowsOnly => Value(config.MessagesOnlyInWindows),
        SelfTestSetting.NewChannelsOpen => Value((int) config.WindowOpening),
        SelfTestSetting.SayWhenTalking => Value(config.VerboseChannelMessages),
        SelfTestSetting.ColourWholeLine => Value(config.ColourWholeLine),
        SelfTestSetting.AdvancedMode => Value(config.AdvancedMode),
        SelfTestSetting.KeepHistory => Value(config.KeepChatLog),
        SelfTestSetting.KeepHistoryUnencrypted => Value(config.KeepChatLogUnencrypted),
        SelfTestSetting.HistoryMegabytes => Value(config.ChatLogMegabytes),
        SelfTestSetting.LocalPrivacyAccepted => Value(config.LocalChatPrivacyAccepted),
        SelfTestSetting.LocalTalkNoteShown => Value(config.LocalChatTalkNoteShown),
        SelfTestSetting.ChatTwoNoteShown => Value(config.ChatTwoLabelNoteShown),
        SelfTestSetting.LocalChatColour => Value(SelfTestValues.Colour(config.LocalChatColour())),
        SelfTestSetting.OwnNameColour => Value(this.OwnNameColour()),
        SelfTestSetting.PartnerNameColour => Value(this.PartnerNameColour()),
        SelfTestSetting.ChannelWindows => Value(config.ForCharacter(contentId).WindowsIfAny(serverUrl) ?? []),
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null),
    };

    public void Write(SelfTestSetting setting, JsonElement value) {
        switch (setting) {
            case SelfTestSetting.WindowsOnly:
                config.MessagesOnlyInWindows = value.GetBoolean();
                break;
            case SelfTestSetting.NewChannelsOpen:
                config.WindowOpening = (WindowOpening) value.GetInt32();
                break;
            case SelfTestSetting.SayWhenTalking:
                config.VerboseChannelMessages = value.GetBoolean();
                break;
            case SelfTestSetting.ColourWholeLine:
                config.ColourWholeLine = value.GetBoolean();
                break;
            case SelfTestSetting.AdvancedMode:
                config.AdvancedMode = value.GetBoolean();
                break;
            case SelfTestSetting.KeepHistory:
                if (sessions != null) {
                    sessions.SetKeepChatLog(value.GetBoolean());
                } else {
                    config.KeepChatLog = value.GetBoolean();
                }

                break;
            case SelfTestSetting.KeepHistoryUnencrypted:
                if (sessions != null) {
                    sessions.SetKeepChatLogUnencrypted(value.GetBoolean());
                } else {
                    config.KeepChatLogUnencrypted = value.GetBoolean();
                }

                break;
            case SelfTestSetting.HistoryMegabytes:
                if (sessions != null) {
                    sessions.SetChatLogMegabytes(value.GetInt32());
                } else {
                    config.ChatLogMegabytes = ChatLogLimits.ClampMegabytes(value.GetInt32());
                }

                break;
            case SelfTestSetting.LocalPrivacyAccepted:
                config.LocalChatPrivacyAccepted = value.GetBoolean();
                break;
            case SelfTestSetting.LocalTalkNoteShown:
                config.LocalChatTalkNoteShown = value.GetBoolean();
                break;
            case SelfTestSetting.ChatTwoNoteShown:
                config.ChatTwoLabelNoteShown = value.GetBoolean();
                break;
            case SelfTestSetting.LocalChatColour:
                config.SetLocalChatColour(SelfTestValues.Colour(value.ValueKind == JsonValueKind.String ? value.GetString() : null));
                break;
            case SelfTestSetting.OwnNameColour:
            case SelfTestSetting.PartnerNameColour:
                var own = value.Deserialize<OwnNameColourValue>() ?? throw new JsonException("No name colour.");
                // Only a number (a Lodestone ID), as every name colour is kept: never anything else.
                if (own.Key.Length == 0 || !own.Key.All(c => char.IsAsciiDigit(c) || c is '-' or '@' or ':' or '/' or '.')) {
                    throw new JsonException("Not a name colour's key.");
                }

                if (own.Rgb is { } rgb) {
                    config.NameColours[own.Key] = rgb & 0xFFFFFF;
                } else {
                    config.NameColours.Remove(own.Key);
                }

                sessions?.NameColoursChanged();
                break;
            case SelfTestSetting.ChannelWindows:
                var layouts = value.Deserialize<List<ChannelWindowLayout>>() ?? [];
                var settings = config.ForCharacter(contentId);
                settings.ChannelWindows[serverUrl] = layouts;
                settings.DropEmptyWindowLists();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(setting), setting, null);
        }

        config.Save();
    }

    /// <summary>The tester's own name colour, keyed as saved: needs the session to know who they are.</summary>
    private OwnNameColourValue OwnNameColour() {
        if (sessions?.Snapshot.Me is not { } me || NameColours.KeyOf(me.UserId, serverUrl) is not { } key) {
            throw new InvalidOperationException("Not logged in to LookingGlass: the name colour's key isn't known.");
        }

        return new OwnNameColourValue(key, config.NameColours.TryGetValue(key, out var rgb) ? rgb : null);
    }

    /// <summary>The other tester's name colour (two players), keyed as saved: needs their user ID.</summary>
    private OwnNameColourValue PartnerNameColour() {
        if (otherUserId == 0 || NameColours.KeyOf(otherUserId, serverUrl) is not { } key) {
            throw new InvalidOperationException("The other tester isn't known: their name colour's key isn't either.");
        }

        return new OwnNameColourValue(key, config.NameColours.TryGetValue(key, out var rgb) ? rgb : null);
    }

    private static JsonElement Value<T>(T value) => SelfTestSettingsSnapshot.Value(value);
}
