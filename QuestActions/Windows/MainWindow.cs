using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.RegularExpressions;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.Interop;
using Lumina.Excel.Sheets;

namespace QuestItemAssistant.Windows;

internal sealed class MainWindow : Window, IDisposable
{
    internal sealed record KeyItemEntry(uint Slot, uint ItemId, string Name, uint IconId, GameInventoryType ContainerType, uint QuestId = 0);
    private sealed record QuestEmoteEntry(uint EmoteId, uint IconId, string Command, string EmoteName, string QuestTitle, uint QuestId);
    private sealed record QuestSayEntry(string Key, string Phrase, string QuestTitle, uint QuestId, string Command = "/say", string ActionLabel = "Say");
    private sealed record EmoteMetadata(string Command, string Name);
    private sealed record EmoteCommandMetadata(uint EmoteId, uint IconId, string Command, string Name);

    private readonly Configuration configuration;
    private readonly System.Action openSettings;
    private readonly List<KeyItemEntry> entries = new();
    private readonly List<KeyItemEntry> allEntries = new();
    private readonly List<QuestEmoteEntry> questEmoteButtons = new();
    private readonly List<QuestSayEntry> questSayButtons = new();
    private readonly Dictionary<uint, EmoteMetadata?> emoteMetadataCache = new();
    private Dictionary<string, EmoteCommandMetadata>? emoteCommandIndex;
    private readonly Dictionary<string, uint> questRuntimeIdCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> questSayMessageCache = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, Dictionary<int, string>> questObjectiveTextCache = new();
    private string? questObjectiveTextCacheLanguage;
    private readonly Dictionary<uint, DateTime> emoteCooldowns = new();
    private readonly HashSet<uint> questsWithKnownZone = new();
    private readonly HashSet<uint> questsInCurrentZone = new();
    private Dictionary<uint, float> questObjectiveDistances = new();
    private uint? selectedItemId;
    private string? selectedEmoteKey;
    private string? selectedSayKey;
    private bool userSelectedAction;
    private uint? lockedItemId;
    private string? lockedEmoteKey;
    private string? lockedSayKey;
    private DateTime lastScan = DateTime.MinValue;
    private DateTime lastEmoteScan = DateTime.MinValue;
    private bool lastUseKeyDown;
    private string scanStatus = "Scanning…";
    private Vector2 previousSize = new(300, 100);
    private bool hasPreviousSize;
    private bool resetPositionRequested;

    private const ImGuiWindowFlags BaseWindowFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

    private static readonly TimeSpan EmoteCooldown = TimeSpan.FromSeconds(5);
    private static readonly Regex SlashCommandRegex = new(@"(?<![\w/])(/[A-Za-z][A-Za-z0-9_-]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuestTodoIndexRegex = new(@"_TODO_(\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal IReadOnlyList<KeyItemEntry> Entries => allEntries;

    public MainWindow(Configuration configuration, System.Action openSettings)
        : base("Quest Actions##QuestItemAssistantHotbar", BaseWindowFlags)
    {
        this.configuration = configuration;
        this.openSettings = openSettings;
        RespectCloseHotkey = false;
        Position = new Vector2(100, 350);
        PositionCondition = ImGuiCond.FirstUseEver;
        Size = new Vector2(100, 100);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public void RequestResetPosition()
    {
        configuration.LockBarPosition = false;
        configuration.Save();
        resetPositionRequested = true;
    }

    public void UpdateVisibility()
    {
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
        {
            if (lastScan != DateTime.MinValue || entries.Count > 0 || questEmoteButtons.Count > 0 || questSayButtons.Count > 0)
            {
                entries.Clear();
                allEntries.Clear();
                questEmoteButtons.Clear();
                questSayButtons.Clear();
                selectedItemId = null;
                selectedEmoteKey = null;
                selectedSayKey = null;
                lastScan = DateTime.MinValue;
                lastEmoteScan = DateTime.MinValue;
            }
            IsOpen = false;
            return;
        }

        if (DateTime.Now - lastScan > TimeSpan.FromSeconds(2))
            RefreshItems();
        if (DateTime.Now - lastEmoteScan > TimeSpan.FromMilliseconds(500))
            RefreshQuestEmoteButtons();

        IsOpen = configuration.ShowHotbar;
    }

    public override void Draw()
    {
        var hasActions = entries.Count > 0 || questEmoteButtons.Count > 0 || questSayButtons.Count > 0;
        var desiredFlags = BaseWindowFlags;
        if (configuration.HideBackgroundPanel || !hasActions)
            desiredFlags |= ImGuiWindowFlags.NoBackground;
        if (!hasActions)
            desiredFlags |= ImGuiWindowFlags.NoInputs;
        if (configuration.LockBarPosition)
            desiredFlags |= ImGuiWindowFlags.NoMove;
        if (Flags != desiredFlags)
            Flags = desiredFlags;

        if (DateTime.Now - lastScan > TimeSpan.FromSeconds(2))
            RefreshItems();
        if (DateTime.Now - lastEmoteScan > TimeSpan.FromMilliseconds(500))
            RefreshQuestEmoteButtons();
        PollUseKey();

        if (resetPositionRequested)
        {
            ImGui.SetWindowPos(new Vector2(100f, 100f), ImGuiCond.Always);
            resetPositionRequested = false;
        }

        hasActions = entries.Count > 0 || questEmoteButtons.Count > 0 || questSayButtons.Count > 0;
        if (hasActions)
            ResizeToContent();
        else
            return;

        var orderedActions = GetOrderedActions();
        var buttonsPerLine = Math.Max(1, configuration.ButtonsPerLine);
        var isPrimaryVertical = configuration.GrowthDirection is BarGrowthDirection.Up or BarGrowthDirection.Down;
        var primaryIsNegative = configuration.GrowthDirection is BarGrowthDirection.Up or BarGrowthDirection.Left;
        var secondaryIsNegative = configuration.SecondaryGrowthDirection is BarGrowthDirection.Up or BarGrowthDirection.Left;
        var iconSize = Math.Clamp(configuration.IconSize, 24f, 128f);
        var style = ImGui.GetStyle();
        var padding = style.WindowPadding;
        var spacing = style.ItemSpacing;
        var actionCount = orderedActions.Count;
        var lineCount = (int)Math.Ceiling(actionCount / (double)buttonsPerLine);
        var labelWidth = 0f;
        if (configuration.ShowNames && isPrimaryVertical && actionCount > 0)
        {
            labelWidth = orderedActions.Max(action => ImGui.CalcTextSize(GetActionLabel(action)).X);
        }
        var cellWidth = iconSize + (configuration.ShowNames && isPrimaryVertical ? spacing.X + labelWidth : 0f);
        var cellHeight = iconSize;

        for (var i = 0; i < orderedActions.Count; i++)
        {
            var action = orderedActions[i];
            var lineIndex = i / buttonsPerLine;
            var slotIndex = i % buttonsPerLine;
            float x;
            float y;

            if (isPrimaryVertical)
            {
                var rowCount = Math.Min(buttonsPerLine, actionCount);
                var row = primaryIsNegative ? rowCount - 1 - slotIndex : slotIndex;
                var column = secondaryIsNegative ? lineCount - 1 - lineIndex : lineIndex;
                x = padding.X + column * (cellWidth + spacing.X);
                y = padding.Y + row * (cellHeight + spacing.Y);
            }
            else
            {
                var columnCount = Math.Min(buttonsPerLine, actionCount);
                var column = primaryIsNegative ? columnCount - 1 - slotIndex : slotIndex;
                var row = secondaryIsNegative ? lineCount - 1 - lineIndex : lineIndex;
                x = padding.X + column * (cellWidth + spacing.X);
                y = padding.Y + row * (cellHeight + spacing.Y);
            }

            ImGui.SetCursorPos(new Vector2(x, y));
            string actionLabel;
            if (action.Item is not null)
            {
                DrawIconButton(action.Item);
                actionLabel = action.Item.Name;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{action.Item.Name}\nItem ID: {action.Item.ItemId}");
            }
            else if (action.Emote is not null)
            {
                DrawQuestEmoteButton(action.Emote);
                actionLabel = $"{action.Emote.EmoteName} ({action.Emote.Command})";
            }
            else if (action.Say is not null)
            {
                DrawQuestSayButton(action.Say);
                actionLabel = $"Say: {action.Say.Phrase}";
            }
            else
            {
                actionLabel = string.Empty;
            }

            if (configuration.ShowNames && isPrimaryVertical)
            {
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(actionLabel);
            }
        }

        if (ImGui.IsWindowHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            RefreshItems();
    }

    private void RefreshQuestEmoteButtons()
    {
        lastEmoteScan = DateTime.Now;
        questEmoteButtons.Clear();
        questSayButtons.Clear();

        var languageName = Plugin.ClientState.ClientLanguage.ToString();
        if (!string.Equals(questObjectiveTextCacheLanguage, languageName, StringComparison.Ordinal))
        {
            questObjectiveTextCache.Clear();
            questObjectiveTextCacheLanguage = languageName;
        }

        try
        {
            unsafe
            {
                var questManager = QuestManager.Instance();
                if (questManager == null)
                    return;

                var questSheet = Plugin.DataManager.GetExcelSheet<Quest>();
                var acceptedQuests = questManager->NormalQuests;
                for (var questSlot = 0; questSlot < acceptedQuests.Length; questSlot++)
                {
                    var runtimeQuestId = acceptedQuests[questSlot].QuestId;
                    if (runtimeQuestId == 0 || !questManager->IsQuestAccepted(runtimeQuestId))
                        continue;
                    if (!IsQuestAllowedByZone(runtimeQuestId))
                        continue;

                    var currentSequence = QuestManager.GetQuestSequence(runtimeQuestId);
                    var questRow = questSheet.GetRowOrDefault((uint)runtimeQuestId + 65536u);
                    if (!questRow.HasValue || string.IsNullOrWhiteSpace(questRow.Value.Id.ToString()))
                        continue;

                    var quest = questRow.Value;
                    var questTitle = quest.Name.ToString();
                    var questSheetId = quest.Id.ToString();
                    if (questSheetId.Length < 5)
                        continue;

                    var folder = questSheetId.Substring(questSheetId.Length - 5, 3);
                    var textSheetName = $"quest/{folder}/{questSheetId}";
                    var todoParams = quest.TodoParams.ToArray();

                    try
                    {
                        if (!questObjectiveTextCache.TryGetValue(runtimeQuestId, out var objectiveTexts))
                        {
                            objectiveTexts = new Dictionary<int, string>();
                            var textSheet = Plugin.DataManager.GetExcelSheet<QuestDialogue>(name: textSheetName);
                            foreach (var textRow in textSheet)
                            {
                                var key = textRow.Key.ToString();
                                var todoIndexMatch = QuestTodoIndexRegex.Match(key);
                                if (!todoIndexMatch.Success ||
                                    !int.TryParse(todoIndexMatch.Groups[1].Value, out var todoIndex) ||
                                    todoIndex < 0 || todoIndex >= todoParams.Length)
                                    continue;

                                var text = textRow.Value.ToString();
                                if (!string.IsNullOrWhiteSpace(text))
                                    objectiveTexts.TryAdd(todoIndex, text);
                            }

                            questObjectiveTextCache[runtimeQuestId] = objectiveTexts;
                        }

                        for (var todoIndex = 0; todoIndex < todoParams.Length; todoIndex++)
                        {
                            if (todoParams[todoIndex].ToDoCompleteSeq != currentSequence ||
                                !objectiveTexts.TryGetValue(todoIndex, out var objectiveText))
                                continue;

                            var sayPhrase = ResolveQuestSayMessage(questTitle, objectiveText);
                            if (!string.IsNullOrWhiteSpace(sayPhrase))
                            {
                                var sayKey = $"{runtimeQuestId}:{sayPhrase}";
                                if (!questSayButtons.Any(x => x.Key == sayKey))
                                    questSayButtons.Add(new QuestSayEntry(sayKey, sayPhrase, questTitle, runtimeQuestId));
                            }

                            foreach (Match match in SlashCommandRegex.Matches(objectiveText))
                            {
                                var command = match.Groups[1].Value;
                                var emote = ResolveEmoteByCommand(command);
                                if (emote == null)
                                    continue;

                                if (!questEmoteButtons.Any(x => x.QuestId == runtimeQuestId &&
                                    string.Equals(x.Command, emote.Command, StringComparison.OrdinalIgnoreCase)))
                                {
                                    questEmoteButtons.Add(new QuestEmoteEntry(
                                        emote.EmoteId,
                                        emote.IconId,
                                        emote.Command,
                                        emote.Name,
                                        questTitle,
                                        runtimeQuestId));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.Debug(ex, "QIA could not inspect quest text sheet {SheetName} for active quest {QuestId}.", textSheetName, runtimeQuestId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "QIA could not discover emote objectives from accepted quests.");
        }

        var activeKeys = questEmoteButtons.Select(GetEmoteKey).ToHashSet(StringComparer.Ordinal);
        foreach (var id in emoteCooldowns.Keys.Where(id => !questEmoteButtons.Any(x => x.EmoteId == id)).ToList())
            emoteCooldowns.Remove(id);

        if (selectedEmoteKey != null && !activeKeys.Contains(selectedEmoteKey))
        {
            selectedEmoteKey = null;
            userSelectedAction = false;
        }
        if (selectedSayKey != null && !questSayButtons.Any(x => x.Key == selectedSayKey))
        {
            selectedSayKey = null;
            userSelectedAction = false;
        }

        UpdateAutomaticSelection();
    }

    private string? ResolveQuestSayMessage(string questTitle, string questDetail)
    {
        var languageName = Plugin.ClientState.ClientLanguage.ToString();
        var cacheKey = questTitle + "\0" + questDetail + "\0" + languageName;
        if (questSayMessageCache.TryGetValue(cacheKey, out var cached))
            return cached;

        string? result = null;
        try
        {
            var quest = Plugin.DataManager.GetExcelSheet<Quest>()
                .FirstOrDefault(x => string.Equals(x.Name.ToString(), questTitle, StringComparison.Ordinal));
            if (quest.RowId != 0)
            {
                var questId = quest.Id.ToString();
                if (questId.Length >= 5)
                {
                    var folder = questId.Substring(questId.Length - 5, 3);
                    var dialogueSheet = Plugin.DataManager.GetExcelSheet<QuestDialogue>(name: $"quest/{folder}/{questId}");
                    var quoteRegex = languageName switch
                    {
                        "Japanese" => new Regex("「([^」]+)」|『([^』]+)』", RegexOptions.Compiled),
                        "German" => new Regex("„([^“]+)“", RegexOptions.Compiled),
                        "Korean" => new Regex("\"([^\"]+)\"", RegexOptions.Compiled),
                        _ => new Regex("“([^”]+)”", RegexOptions.Compiled),
                    };

                    foreach (var dialogue in dialogueSheet)
                    {
                        var key = dialogue.Key.ToString();
                        if (!Regex.IsMatch(key, "_(SAY|SAYTODO|SYSTEM)_") || dialogue.Value.IsEmpty)
                            continue;

                        var phrase = dialogue.Value.ToString();
                        if (quoteRegex.Matches(questDetail).Cast<Match>()
                            .SelectMany(m => m.Groups.Cast<Group>().Skip(1))
                            .Any(g => g.Success && g.Value.Contains(phrase, StringComparison.Ordinal)))
                        {
                            result = phrase;
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "QIA could not resolve a required /say phrase for quest '{QuestTitle}'.", questTitle);
        }

        questSayMessageCache[cacheKey] = result;
        return result;
    }

    private uint ResolveQuestRuntimeId(string questTitle)
    {
        if (questRuntimeIdCache.TryGetValue(questTitle, out var cached))
            return cached;

        uint runtimeQuestId = 0;
        try
        {
            var questSheet = Plugin.DataManager.GetExcelSheet<Quest>();
            foreach (var quest in questSheet)
            {
                if (!string.Equals(quest.Name.ToString(), questTitle, StringComparison.Ordinal))
                    continue;

                runtimeQuestId = quest.RowId > ushort.MaxValue ? quest.RowId - 65536u : quest.RowId;
                break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "QIA could not resolve native quest title '{QuestTitle}' to a quest ID.", questTitle);
        }

        questRuntimeIdCache[questTitle] = runtimeQuestId;
        return runtimeQuestId;
    }

    private EmoteCommandMetadata? ResolveEmoteByCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        if (emoteCommandIndex == null)
        {
            var index = new Dictionary<string, EmoteCommandMetadata>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var row in Plugin.DataManager.GetExcelSheet<Emote>())
                {
                    var resolvedCommand = ResolveEmoteCommandFromTextCommand(row);
                    if (string.IsNullOrWhiteSpace(resolvedCommand))
                        continue;

                    var iconId = 0u;
                    try
                    {
                        var iconValue = typeof(Emote).GetProperty("Icon", BindingFlags.Instance | BindingFlags.Public)?.GetValue(row);
                        if (iconValue != null)
                            iconId = Convert.ToUInt32(iconValue);
                    }
                    catch
                    {
                    }

                    var name = row.Name.ToString();
                    index.TryAdd(resolvedCommand, new EmoteCommandMetadata(row.RowId, iconId, resolvedCommand,
                        string.IsNullOrWhiteSpace(name) ? resolvedCommand : name));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug(ex, "QIA could not build the Lumina emote-command index.");
            }

            emoteCommandIndex = index;
        }

        return emoteCommandIndex.TryGetValue(command.Trim(), out var metadata) ? metadata : null;
    }

    private EmoteMetadata? ResolveEmoteMetadata(uint emoteId)
    {
        if (emoteMetadataCache.TryGetValue(emoteId, out var cached))
            return cached;

        EmoteMetadata? metadata = null;
        try
        {
            var emoteSheet = Plugin.DataManager.GetExcelSheet<Emote>();
            if (emoteSheet.TryGetRow(emoteId, out var emoteRow))
            {
                var command = ResolveEmoteCommandFromTextCommand(emoteRow);
                var name = emoteRow.Name.ToString();
                if (!string.IsNullOrWhiteSpace(command))
                    metadata = new EmoteMetadata(command, string.IsNullOrWhiteSpace(name) ? command : name);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "QIA could not resolve emote row {EmoteId} from Lumina.", emoteId);
        }

        emoteMetadataCache[emoteId] = metadata;
        return metadata;
    }

    private static string? ResolveEmoteCommandFromTextCommand(Emote emoteRow)
    {
        var textCommandProperty = typeof(Emote).GetProperty("TextCommand", BindingFlags.Instance | BindingFlags.Public);
        if (textCommandProperty == null)
            return null;

        var textCommandRef = textCommandProperty.GetValue(emoteRow);
        if (textCommandRef == null)
            return null;

        var valueProperty = textCommandRef.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
        var textCommandRow = valueProperty?.GetValue(textCommandRef);
        if (textCommandRow == null)
            return null;

        var commandProperty = textCommandRow.GetType().GetProperty("Command", BindingFlags.Instance | BindingFlags.Public);
        var commandValue = commandProperty?.GetValue(textCommandRow);
        var command = commandValue?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return null;

        if (!command.StartsWith('/'))
            command = "/" + command;
        return command;
    }

    private void DrawQuestEmoteButton(QuestEmoteEntry entry)
    {
        var now = DateTime.UtcNow;
        var coolingDown = emoteCooldowns.TryGetValue(entry.EmoteId, out var cooldownUntil) && now < cooldownUntil;
        var isSelected = selectedEmoteKey == GetEmoteKey(entry);
        var size = Math.Clamp(configuration.IconSize, 24f, 128f);
        ImGui.PushID($"quest-emote-{entry.EmoteId}-{entry.QuestTitle}");
        if (isSelected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f, 0.42f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.52f, 0.28f, 1f));
        }
        if (coolingDown) ImGui.BeginDisabled();
        var clicked = ImGui.Button("##quest-emote", new Vector2(size, size));
        var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (coolingDown) ImGui.EndDisabled();

        if (rightClicked)
            ToggleEmoteSelectionLock(entry);

        if (clicked)
        {
            selectedEmoteKey = GetEmoteKey(entry);
            selectedItemId = null;
            selectedSayKey = null;
            userSelectedAction = false;
            TryPerformEmote(entry);
            ResumeAutomaticSelectionAfterActionClick();
        }

        if (entry.IconId != 0 && Plugin.TextureProvider.TryGetFromGameIcon(new GameIconLookup(entry.IconId), out var texture) && texture != null)
        {
            var wrap = texture.GetWrapOrEmpty();
            if (wrap.Handle != nint.Zero)
                ImGui.GetWindowDrawList().AddImage(wrap.Handle, min + new Vector2(3, 3), max - new Vector2(3, 3));
        }
        if (isSelected)
        {
            var borderColor = lockedEmoteKey == GetEmoteKey(entry)
                ? new Vector4(1f, 0.78f, 0.2f, 1f)
                : new Vector4(0.3f, 1f, 0.4f, 1f);
            ImGui.GetWindowDrawList().AddRect(min, max, ImGui.GetColorU32(borderColor), 2f, ImDrawFlags.None, 2f);
            if (configuration.UseKey != 0)
            {
                var keyLabel = ((VirtualKey)configuration.UseKey).ToString();
                var textSize = ImGui.CalcTextSize(keyLabel);
                ImGui.GetWindowDrawList().AddRectFilled(max - textSize - new Vector2(5, 3), max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f)));
                ImGui.GetWindowDrawList().AddText(max - textSize - new Vector2(2, 2), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), keyLabel);
            }
            ImGui.PopStyleColor(2);
        }

        if (ImGui.IsItemHovered())
        {
            var remaining = coolingDown ? Math.Max(0, (cooldownUntil - now).TotalSeconds) : 0;
            var target = lockedEmoteKey == GetEmoteKey(entry)
                ? "\nSelection locked — right-click to unlock"
                : isSelected ? "\nHotkey target\nRight-click to lock selection" : "\nRight-click to lock selection";
            ImGui.SetTooltip(coolingDown
                ? $"{entry.EmoteName} ({entry.Command})\nQuest: {entry.QuestTitle}{target}\nCooldown: {remaining:0}s"
                : $"{entry.EmoteName} ({entry.Command})\nQuest: {entry.QuestTitle}{target}\nPress the configured hotkey to perform");
        }
        ImGui.PopID();
    }

    private void DrawQuestSayButton(QuestSayEntry entry)
    {
        var isSelected = selectedSayKey == entry.Key;
        var size = Math.Clamp(configuration.IconSize, 24f, 128f);
        ImGui.PushID($"quest-say-{entry.Key}");
        if (isSelected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f, 0.42f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.52f, 0.28f, 1f));
        }

        var clicked = ImGui.Button("##quest-say", new Vector2(size, size));
        var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (rightClicked)
            ToggleSaySelectionLock(entry);
        if (clicked)
        {
            selectedSayKey = entry.Key;
            selectedItemId = null;
            selectedEmoteKey = null;
            userSelectedAction = false;
            TrySendSay(entry);
            ResumeAutomaticSelectionAfterActionClick();
        }

        const uint converseIconId = 246320;
        if (Plugin.TextureProvider.TryGetFromGameIcon(new GameIconLookup(converseIconId), out var texture) && texture != null)
        {
            var wrap = texture.GetWrapOrEmpty();
            if (wrap.Handle != nint.Zero)
                ImGui.GetWindowDrawList().AddImage(wrap.Handle, min + new Vector2(3, 3), max - new Vector2(3, 3));
        }

        if (isSelected)
        {
            var borderColor = lockedSayKey == entry.Key
                ? new Vector4(1f, 0.78f, 0.2f, 1f)
                : new Vector4(0.3f, 1f, 0.4f, 1f);
            ImGui.GetWindowDrawList().AddRect(min, max, ImGui.GetColorU32(borderColor), 2f, ImDrawFlags.None, 2f);
            if (configuration.UseKey != 0)
            {
                var keyLabel = ((VirtualKey)configuration.UseKey).ToString();
                var textSize = ImGui.CalcTextSize(keyLabel);
                ImGui.GetWindowDrawList().AddRectFilled(max - textSize - new Vector2(5, 3), max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f)));
                ImGui.GetWindowDrawList().AddText(max - textSize - new Vector2(2, 2), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), keyLabel);
            }
            ImGui.PopStyleColor(2);
        }

        if (ImGui.IsItemHovered())
        {
            var actionDescription = entry.Command == "/say" ? $"Say: {entry.Phrase}" : $"Emote command: {entry.Command}";
            var target = lockedSayKey == entry.Key
                ? "Selection locked — right-click to unlock"
                : isSelected ? "Hotkey target\nRight-click to lock selection" : "Right-click to lock selection";
            ImGui.SetTooltip($"{actionDescription}\nQuest: {entry.QuestTitle}\n{target}");
        }
        ImGui.PopID();
    }

    private unsafe void TrySendSay(QuestSayEntry entry)
    {
        Utf8String* commandText = null;
        try
        {
            string command;
            if (entry.Command == "/say")
            {
                if (string.IsNullOrWhiteSpace(entry.Phrase) || entry.Phrase.Contains('\n') || entry.Phrase.Contains('\r'))
                    throw new InvalidOperationException("The resolved quest /say phrase is empty or contains a line break.");
                command = "/say " + entry.Phrase;
            }
            else if (entry.Command == "/me")
            {
                command = "/me";
            }
            else
            {
                throw new InvalidOperationException($"Unsupported quest chat command: {entry.Command}");
            }

            commandText = Utf8String.FromString(command);
            commandText->SanitizeString(AllowedEntities.Numbers | AllowedEntities.UppercaseLetters |
                                       AllowedEntities.LowercaseLetters | AllowedEntities.OtherCharacters |
                                       AllowedEntities.SpecialCharacters);
            if (commandText->StringLength > 500)
            {
                Plugin.Log.Warning("[QIA-SAY] Skipping an overlong quest /say phrase for {QuestTitle}.", entry.QuestTitle);
                return;
            }

            var uiModule = UIModule.Instance();
            if (uiModule == null)
                throw new InvalidOperationException("The native UI module is unavailable.");

            uiModule->ProcessChatBoxEntry(commandText);
            Plugin.Log.Information("[QIA-CHAT] Submitted quest command {Command} for {QuestTitle}.", entry.Command, entry.QuestTitle);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[QIA-SAY] Failed to submit required quest /say phrase for {QuestTitle}.", entry.QuestTitle);
        }
        finally
        {
            if (commandText != null)
                commandText->Dtor(true);
        }
    }

    private static string GetEmoteKey(QuestEmoteEntry entry) => $"{entry.EmoteId}:{entry.QuestTitle}";

    private void ResumeAutomaticSelectionAfterActionClick()
    {
        userSelectedAction = false;
        UpdateAutomaticSelection();
    }

    private void UpdateAutomaticSelection()
    {
        if (lockedItemId is uint lockedId && entries.Any(x => x.ItemId == lockedId))
        {
            selectedItemId = lockedId;
            selectedEmoteKey = null;
            selectedSayKey = null;
            return;
        }
        if (lockedEmoteKey is not null && questEmoteButtons.Any(x => GetEmoteKey(x) == lockedEmoteKey))
        {
            selectedItemId = null;
            selectedEmoteKey = lockedEmoteKey;
            selectedSayKey = null;
            return;
        }
        if (lockedSayKey is not null && questSayButtons.Any(x => x.Key == lockedSayKey))
        {
            selectedItemId = null;
            selectedEmoteKey = null;
            selectedSayKey = lockedSayKey;
            return;
        }

        if (userSelectedAction)
        {
            if (selectedEmoteKey != null && questEmoteButtons.Any(x => GetEmoteKey(x) == selectedEmoteKey))
                return;
            if (selectedSayKey != null && questSayButtons.Any(x => x.Key == selectedSayKey))
                return;
            if (selectedItemId is uint selectedId && entries.Any(x => x.ItemId == selectedId))
                return;
            userSelectedAction = false;
        }

        if (!configuration.SortByQuestProximity)
        {
            selectedEmoteKey = null;
            selectedSayKey = null;
            selectedItemId = GetPreferredEntry()?.ItemId;
            if (selectedItemId == null && questSayButtons.Count > 0)
                selectedSayKey = questSayButtons[0].Key;
            else if (selectedItemId == null && questEmoteButtons.Count > 0)
                selectedEmoteKey = GetEmoteKey(questEmoteButtons[0]);
            return;
        }

        KeyItemEntry? closestItem = null;
        QuestEmoteEntry? closestEmote = null;
        QuestSayEntry? closestSay = null;
        var closestDistance = float.PositiveInfinity;

        foreach (var entry in entries)
        {
            if (entry.ItemId == WondrousTailsItemId || entry.QuestId == 0)
                continue;
            if (questObjectiveDistances.TryGetValue(entry.QuestId, out var distance) && distance < closestDistance)
            {
                closestDistance = distance;
                closestItem = entry;
                closestEmote = null;
                closestSay = null;
            }
        }

        foreach (var emote in questEmoteButtons)
        {
            if (emote.QuestId == 0)
                continue;
            if (questObjectiveDistances.TryGetValue(emote.QuestId, out var distance) && distance < closestDistance)
            {
                closestDistance = distance;
                closestItem = null;
                closestEmote = emote;
                closestSay = null;
            }
        }

        foreach (var say in questSayButtons)
        {
            if (say.QuestId == 0)
                continue;
            if (questObjectiveDistances.TryGetValue(say.QuestId, out var distance) && distance < closestDistance)
            {
                closestDistance = distance;
                closestItem = null;
                closestEmote = null;
                closestSay = say;
            }
        }

        if (closestItem != null)
        {
            selectedItemId = closestItem.ItemId;
            selectedEmoteKey = null;
            selectedSayKey = null;
            return;
        }
        if (closestEmote != null)
        {
            selectedItemId = null;
            selectedEmoteKey = GetEmoteKey(closestEmote);
            selectedSayKey = null;
            return;
        }
        if (closestSay != null)
        {
            selectedItemId = null;
            selectedEmoteKey = null;
            selectedSayKey = closestSay.Key;
            return;
        }

        var fallbackItem = GetPreferredEntry();
        if (fallbackItem != null && fallbackItem.ItemId != WondrousTailsItemId)
        {
            selectedItemId = fallbackItem.ItemId;
            selectedEmoteKey = null;
            selectedSayKey = null;
        }
        else if (questSayButtons.Count > 0)
        {
            selectedItemId = null;
            selectedEmoteKey = null;
            selectedSayKey = questSayButtons[0].Key;
        }
        else if (questEmoteButtons.Count > 0)
        {
            selectedItemId = null;
            selectedEmoteKey = GetEmoteKey(questEmoteButtons[0]);
            selectedSayKey = null;
        }
        else
        {
            selectedItemId = fallbackItem?.ItemId;
            selectedEmoteKey = null;
            selectedSayKey = null;
        }
    }

    private sealed record OrderedAction(KeyItemEntry? Item, QuestEmoteEntry? Emote, QuestSayEntry? Say, float Distance, bool IsWondrousTails, int OriginalIndex);

    private List<OrderedAction> GetOrderedActions()
    {
        var actions = new List<OrderedAction>(entries.Count + questEmoteButtons.Count + questSayButtons.Count);
        var index = 0;
        foreach (var entry in entries)
        {
            var distance = questObjectiveDistances.TryGetValue(entry.QuestId, out var d) ? d : float.PositiveInfinity;
            actions.Add(new OrderedAction(entry, null, null, distance, entry.ItemId == WondrousTailsItemId, index++));
        }
        foreach (var emote in questEmoteButtons)
        {
            var distance = questObjectiveDistances.TryGetValue(emote.QuestId, out var d) ? d : float.PositiveInfinity;
            actions.Add(new OrderedAction(null, emote, null, distance, false, index++));
        }
        foreach (var say in questSayButtons)
        {
            var distance = questObjectiveDistances.TryGetValue(say.QuestId, out var d) ? d : float.PositiveInfinity;
            actions.Add(new OrderedAction(null, null, say, distance, false, index++));
        }

        if (configuration.SortByQuestProximity)
        {
            actions = actions
                .OrderBy(x => x.IsWondrousTails ? 1 : 0)
                .ThenBy(x => x.Distance)
                .ThenBy(x => x.OriginalIndex)
                .ToList();
        }

        var wondrous = actions.FirstOrDefault(x => x.IsWondrousTails);
        if (wondrous is not null)
        {
            actions.Remove(wondrous);
            actions.Add(wondrous);
        }

        return actions;
    }

    private static string GetActionLabel(OrderedAction action) => action.Item is not null
        ? action.Item.Name
        : action.Emote is not null
            ? $"{action.Emote.EmoteName} ({action.Emote.Command})"
            : action.Say is not null
                ? $"Say: {action.Say.Phrase}"
                : string.Empty;

    private bool IsHorizontal() => configuration.GrowthDirection is BarGrowthDirection.Left or BarGrowthDirection.Right;

    private const uint WondrousTailsItemId = 2002023;

    private List<KeyItemEntry> GetOrderedEntries()
    {
        var result = entries.ToList();
        if (configuration.SortByQuestProximity)
        {
            var distances = questObjectiveDistances;
            result = result
                .Select((entry, index) => new
                {
                    Entry = entry,
                    OriginalIndex = index,
                    IsWondrousTails = entry.ItemId == WondrousTailsItemId,
                    Distance = distances.TryGetValue(entry.QuestId, out var distance) ? distance : float.PositiveInfinity,
                })
                .OrderBy(x => x.IsWondrousTails ? 1 : 0)
                .ThenBy(x => x.Distance)
                .ThenBy(x => x.OriginalIndex)
                .Select(x => x.Entry)
                .ToList();
        }
        var wondrousTails = result.FirstOrDefault(x => x.ItemId == WondrousTailsItemId);
        if (wondrousTails is not null)
        {
            result.Remove(wondrousTails);
            result.Add(wondrousTails);
        }
        return result;
    }

    private KeyItemEntry? GetPreferredEntry()
    {
        if (entries.Count == 0) return null;
        var candidates = entries.Where(x => x.ItemId != WondrousTailsItemId).ToList();
        if (candidates.Count == 0) return entries.FirstOrDefault(x => x.ItemId == WondrousTailsItemId);
        if (!configuration.SortByQuestProximity) return candidates[0];

        return candidates
            .Select((entry, index) => new
            {
                Entry = entry,
                OriginalIndex = index,
                Distance = questObjectiveDistances.TryGetValue(entry.QuestId, out var distance) ? distance : float.PositiveInfinity,
            })
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.OriginalIndex)
            .Select(x => x.Entry)
            .FirstOrDefault();
    }

    private Dictionary<uint, float> GetQuestObjectiveDistances()
    {
        var distances = new Dictionary<uint, float>();
        try
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null)
            {
                return distances;
            }

            var territoryId = Plugin.ClientState.TerritoryType;
            var position = player.Position;

            questsWithKnownZone.Clear();
            questsInCurrentZone.Clear();
            unsafe
            {
                var nativeMap = FFXIVClientStructs.FFXIV.Client.Game.UI.Map.Instance();
                if (nativeMap == null)
                {
                        return distances;
                }

                foreach (var group in nativeMap->QuestMarkers)
                {
                    var questId = group.ObjectiveId;
                    if (questId == 0) continue;

                    var markerData = group.MarkerData.ToList<MapMarkerData>();
                    if (markerData.Count > 0)
                        questsWithKnownZone.Add(questId);

                    foreach (var marker in markerData)
                    {
                        if (marker.TerritoryTypeId != territoryId) continue;

                        questsInCurrentZone.Add(questId);
                        if (!group.ShouldRender) continue;
                        var dx = marker.Position.X - position.X;
                        var dz = marker.Position.Z - position.Z;
                        var distanceSquared = dx * dx + dz * dz;
                        if (!distances.TryGetValue(questId, out var existing) || distanceSquared < existing)
                            distances[questId] = distanceSquared;
                    }
                }
            }

        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "QIA could not resolve native quest objective distances; preserving inventory order for unresolved items.");
        }
        return distances;
    }

    private bool IsQuestAllowedByZone(uint questId, bool alwaysShow = false)
    {
        if (alwaysShow || !configuration.FilterActionsByCurrentZone || questId == 0)
            return true;
        if (questsInCurrentZone.Contains(questId))
            return true;
        if (questsWithKnownZone.Contains(questId))
            return false;
        return configuration.ShowActionsWithUnknownZone;
    }

    private void ResizeToContent()
    {
        var style = ImGui.GetStyle();
        var padding = style.WindowPadding;
        var spacing = style.ItemSpacing;
        var iconSize = Math.Clamp(configuration.IconSize, 24f, 128f);
        float width;
        float height;

        var buttonCount = entries.Count + questEmoteButtons.Count + questSayButtons.Count;
        if (buttonCount == 0)
        {
            width = padding.X * 2;
            height = padding.Y * 2;
        }
        else
        {
            var buttonsPerLine = Math.Max(1, configuration.ButtonsPerLine);
            var labelsWidth = 0f;
            if (!IsHorizontal() && configuration.ShowNames && buttonCount > 0)
            {
                var labels = new List<string>(buttonCount);
                labels.AddRange(entries.Select(x => x.Name));
                labels.AddRange(questEmoteButtons.Select(x => $"{x.EmoteName} ({x.Command})"));
                labels.AddRange(questSayButtons.Select(x => $"Say: {x.Phrase}"));
                if (labels.Count > 0)
                    labelsWidth = spacing.X + labels.Max(x => ImGui.CalcTextSize(x).X);
            }

            if (IsHorizontal())
            {
                var columns = Math.Min(buttonCount, buttonsPerLine);
                var rows = (int)Math.Ceiling(buttonCount / (double)buttonsPerLine);
                width = padding.X * 2 + columns * iconSize + Math.Max(0, columns - 1) * spacing.X;
                height = padding.Y * 2 + rows * iconSize + Math.Max(0, rows - 1) * spacing.Y;
            }
            else
            {
                var rows = Math.Min(buttonCount, buttonsPerLine);
                var columns = (int)Math.Ceiling(buttonCount / (double)buttonsPerLine);
                var columnWidth = iconSize + labelsWidth;
                width = padding.X * 2 + columns * columnWidth + Math.Max(0, columns - 1) * spacing.X;
                height = padding.Y * 2 + rows * iconSize + Math.Max(0, rows - 1) * spacing.Y;
            }
        }

        var target = new Vector2(MathF.Ceiling(width), MathF.Ceiling(height));
        var current = ImGui.GetWindowSize();

        if (!hasPreviousSize)
        {
            previousSize = current;
            hasPreviousSize = true;
        }

        var sizeChanged =
            MathF.Abs(previousSize.X - target.X) > 1f ||
            MathF.Abs(previousSize.Y - target.Y) > 1f;

        if (sizeChanged)
        {
            var pos = ImGui.GetWindowPos();
            var growsLeft = configuration.GrowthDirection == BarGrowthDirection.Left
                || ((configuration.GrowthDirection is BarGrowthDirection.Up or BarGrowthDirection.Down)
                    && configuration.SecondaryGrowthDirection == BarGrowthDirection.Left);
            var growsUp = configuration.GrowthDirection == BarGrowthDirection.Up
                || ((configuration.GrowthDirection is BarGrowthDirection.Left or BarGrowthDirection.Right)
                    && configuration.SecondaryGrowthDirection == BarGrowthDirection.Up);

            if (growsLeft)
                pos.X += previousSize.X - target.X;
            if (growsUp)
                pos.Y += previousSize.Y - target.Y;

            ImGui.SetWindowPos(pos, ImGuiCond.Always);
        }

        if (MathF.Abs(current.X - target.X) > 1f ||
            MathF.Abs(current.Y - target.Y) > 1f)
        {
            ImGui.SetWindowSize(target, ImGuiCond.Always);
        }

        previousSize = target;
    }

    private void DrawIconButton(KeyItemEntry entry)
    {
        var isSelected = selectedItemId == entry.ItemId;
        var size = Math.Clamp(configuration.IconSize, 24f, 128f);
        ImGui.PushID($"item-{entry.ItemId}-{entry.Slot}");
        if (isSelected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f, 0.42f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.52f, 0.28f, 1f));
        }

        if (ImGui.Button("##item", new Vector2(size, size)))
        {
            selectedItemId = entry.ItemId;
            selectedEmoteKey = null;
            selectedSayKey = null;
            userSelectedAction = false;
            TryUseItem(entry);
            ResumeAutomaticSelectionAfterActionClick();
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ToggleSelectionLock(entry);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (entry.IconId != 0 && Plugin.TextureProvider.TryGetFromGameIcon(new GameIconLookup(entry.IconId), out var texture) && texture != null)
        {
            var wrap = texture.GetWrapOrEmpty();
            if (wrap.Handle != nint.Zero)
                ImGui.GetWindowDrawList().AddImage(wrap.Handle, min + new Vector2(3, 3), max - new Vector2(3, 3));
        }

        if (isSelected)
        {
            var borderColor = lockedItemId == entry.ItemId
                ? new Vector4(1f, 0.78f, 0.2f, 1f)
                : new Vector4(0.3f, 1f, 0.4f, 1f);
            ImGui.GetWindowDrawList().AddRect(min, max, ImGui.GetColorU32(borderColor), 2f, ImDrawFlags.None, 2f);
            if (configuration.UseKey != 0)
            {
                var keyLabel = ((VirtualKey)configuration.UseKey).ToString();
                var textSize = ImGui.CalcTextSize(keyLabel);
                ImGui.GetWindowDrawList().AddRectFilled(max - textSize - new Vector2(5, 3), max, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f)));
                ImGui.GetWindowDrawList().AddText(max - textSize - new Vector2(2, 2), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), keyLabel);
            }
            ImGui.PopStyleColor(2);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(lockedItemId == entry.ItemId
                ? $"{entry.Name}\nSelection locked — right-click to unlock"
                : $"{entry.Name}\nRight-click to lock selection");
        ImGui.PopID();

    }

    private void ClearSelectionLocks()
    {
        lockedItemId = null;
        lockedEmoteKey = null;
        lockedSayKey = null;
    }

    private void ToggleSelectionLock(KeyItemEntry entry)
    {
        if (lockedItemId == entry.ItemId)
        {
            ClearSelectionLocks();
            userSelectedAction = false;
            UpdateAutomaticSelection();
            Plugin.Log.Information("[QIA] Selection unlocked; automatic selection resumed.");
            return;
        }

        ClearSelectionLocks();
        lockedItemId = entry.ItemId;
        selectedItemId = entry.ItemId;
        selectedEmoteKey = null;
        selectedSayKey = null;
        userSelectedAction = true;
        Plugin.Log.Information("[QIA] Selection locked to item {Name} (ID {ItemId}).", entry.Name, entry.ItemId);
    }

    private void ToggleEmoteSelectionLock(QuestEmoteEntry entry)
    {
        var key = GetEmoteKey(entry);
        if (lockedEmoteKey == key)
        {
            ClearSelectionLocks();
            userSelectedAction = false;
            UpdateAutomaticSelection();
            Plugin.Log.Information("[QIA] Emote selection unlocked; automatic selection resumed.");
            return;
        }

        ClearSelectionLocks();
        lockedEmoteKey = key;
        selectedItemId = null;
        selectedEmoteKey = key;
        selectedSayKey = null;
        userSelectedAction = true;
        Plugin.Log.Information("[QIA] Selection locked to emote {Command} for quest {QuestTitle}.", entry.Command, entry.QuestTitle);
    }

    private void ToggleSaySelectionLock(QuestSayEntry entry)
    {
        if (lockedSayKey == entry.Key)
        {
            ClearSelectionLocks();
            userSelectedAction = false;
            UpdateAutomaticSelection();
            Plugin.Log.Information("[QIA] Chat-action selection unlocked; automatic selection resumed.");
            return;
        }

        ClearSelectionLocks();
        lockedSayKey = entry.Key;
        selectedItemId = null;
        selectedEmoteKey = null;
        selectedSayKey = entry.Key;
        userSelectedAction = true;
        Plugin.Log.Information("[QIA] Selection locked to chat action for quest {QuestTitle}.", entry.QuestTitle);
    }

    private void MaintainSelectionLock()
    {
        if (lockedItemId is uint lockedId)
        {
            var lockedEntry = entries.FirstOrDefault(x => x.ItemId == lockedId);
            if (lockedEntry is null)
            {
                ClearSelectionLocks();
                userSelectedAction = false;
                Plugin.Log.Information("[QIA] Selection lock released because the item is no longer available.");
                return;
            }

            if (lockedEntry.QuestId != 0 && !IsQuestStillAccepted(lockedEntry.QuestId))
            {
                ClearSelectionLocks();
                userSelectedAction = false;
                Plugin.Log.Information("[QIA] Selection lock released because the associated quest is no longer active: {Name} (ID {ItemId}).", lockedEntry.Name, lockedEntry.ItemId);
                return;
            }

            selectedItemId = lockedId;
            return;
        }

        if (lockedEmoteKey is not null)
        {
            var lockedEmote = questEmoteButtons.FirstOrDefault(x => GetEmoteKey(x) == lockedEmoteKey);
            if (lockedEmote is null || (lockedEmote.QuestId != 0 && !IsQuestStillAccepted(lockedEmote.QuestId)))
            {
                ClearSelectionLocks();
                userSelectedAction = false;
                Plugin.Log.Information("[QIA] Emote selection lock released because its quest action is no longer available.");
                return;
            }
            selectedItemId = null;
            selectedEmoteKey = lockedEmoteKey;
            selectedSayKey = null;
            return;
        }

        if (lockedSayKey is not null)
        {
            var lockedSay = questSayButtons.FirstOrDefault(x => x.Key == lockedSayKey);
            if (lockedSay is null || (lockedSay.QuestId != 0 && !IsQuestStillAccepted(lockedSay.QuestId)))
            {
                ClearSelectionLocks();
                userSelectedAction = false;
                Plugin.Log.Information("[QIA] Chat-action selection lock released because its quest action is no longer available.");
                return;
            }
            selectedItemId = null;
            selectedEmoteKey = null;
            selectedSayKey = lockedSayKey;
        }
    }

    private static unsafe bool IsQuestStillAccepted(uint questId)
    {
        var journal = Journal.Instance();
        return journal == null || journal->IsQuestAccepted((ushort)questId);
    }

    private void PollUseKey()
    {
        var down = configuration.UseKey != 0 && Plugin.KeyState[(VirtualKey)configuration.UseKey];
        if (down && !lastUseKeyDown)
        {
            if (lockedItemId is uint lockedId)
            {
                var locked = entries.FirstOrDefault(x => x.ItemId == lockedId);
                if (locked != null)
                {
                    selectedItemId = locked.ItemId;
                    selectedEmoteKey = null;
                    selectedSayKey = null;
                    TryUseItem(locked);
                    lastUseKeyDown = down;
                    return;
                }
            }

            if (lockedEmoteKey is not null)
            {
                var lockedEmote = questEmoteButtons.FirstOrDefault(x => GetEmoteKey(x) == lockedEmoteKey);
                if (lockedEmote is not null)
                {
                    selectedItemId = null;
                    selectedEmoteKey = lockedEmoteKey;
                    selectedSayKey = null;
                    TryPerformEmote(lockedEmote);
                    lastUseKeyDown = down;
                    return;
                }
            }
            if (lockedSayKey is not null)
            {
                var lockedSay = questSayButtons.FirstOrDefault(x => x.Key == lockedSayKey);
                if (lockedSay is not null)
                {
                    selectedItemId = null;
                    selectedEmoteKey = null;
                    selectedSayKey = lockedSayKey;
                    TrySendSay(lockedSay);
                    lastUseKeyDown = down;
                    return;
                }
            }

            userSelectedAction = false;
            var target = GetOrderedActions().FirstOrDefault();
            if (target is not null)
            {
                selectedItemId = target.Item?.ItemId;
                selectedEmoteKey = target.Emote is null ? null : GetEmoteKey(target.Emote);
                selectedSayKey = target.Say?.Key;

                if (target.Say is not null)
                {
                    TrySendSay(target.Say);
                }
                else if (target.Emote is not null)
                {
                    Plugin.Log.Debug("[QIA-EMOTE] Configured key pressed; ordered priority command={Command}, quest={QuestTitle}.", target.Emote.Command, target.Emote.QuestTitle);
                    TryPerformEmote(target.Emote);
                }
                else if (target.Item is not null)
                {
                    TryUseItem(target.Item);
                }
            }
        }
        lastUseKeyDown = down;
    }

    private unsafe void TryPerformEmote(QuestEmoteEntry entry)
    {
        var now = DateTime.UtcNow;
        if (emoteCooldowns.TryGetValue(entry.EmoteId, out var cooldownUntil) && now < cooldownUntil)
            return;

        Utf8String* commandText = null;
        try
        {
            if (string.IsNullOrWhiteSpace(entry.Command) || !entry.Command.StartsWith('/'))
                throw new InvalidOperationException($"Invalid game command for quest emote: '{entry.Command}'.");

            Plugin.Log.Information("[QIA-EMOTE] Executing command {Command} for quest {QuestTitle} (emote row {EmoteId}).", entry.Command, entry.QuestTitle, entry.EmoteId);

            commandText = Utf8String.FromString(entry.Command);
            var uiModule = UIModule.Instance();
            if (uiModule == null)
                throw new InvalidOperationException("The native UI module is unavailable.");

            uiModule->ProcessChatBoxEntry(commandText);
            emoteCooldowns[entry.EmoteId] = now + EmoteCooldown;
            Plugin.Log.Information("[QIA-EMOTE] Submitted native game command: {Command} for {QuestTitle}.", entry.Command, entry.QuestTitle);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[QIA-EMOTE] Failed to execute native game command {Command}.", entry.Command);
        }
        finally
        {
            if (commandText != null)
                commandText->Dtor(true);
        }
    }

    private void TryUseItem(KeyItemEntry entry)
    {
        try
        {
            unsafe
            {
                var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance();
                if (agent == null)
                {
                    Plugin.Log.Warning("Could not use {Name}: inventory agent unavailable.", entry.Name);
                    return;
                }

                agent->UseItem(entry.ItemId, (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)entry.ContainerType, entry.Slot);
                Plugin.Log.Information("Requested use of key item {Name} (ID {ItemId}, slot {Slot}).", entry.Name, entry.ItemId, entry.Slot);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to request use of key item {Name} (ID {ItemId}).", entry.Name, entry.ItemId);
        }
    }

    private void RefreshItems()
    {
        entries.Clear();
        allEntries.Clear();
        try
        {
            questObjectiveDistances = configuration.SortByQuestProximity || configuration.FilterActionsByCurrentZone
                ? GetQuestObjectiveDistances()
                : new Dictionary<uint, float>();

            var eventItemSheet = Plugin.DataManager.GetExcelSheet<EventItem>();
            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            foreach (var item in Plugin.GameInventory.GetInventoryItems(GameInventoryType.KeyItems))
            {
                if (item.IsEmpty || item.ItemId == 0) continue;
                var id = item.BaseItemId != 0 ? item.BaseItemId : item.ItemId;
                string name;
                uint iconId;
                uint questId = 0;
                if (eventItemSheet.TryGetRow(id, out var eventRow))
                {
                    name = eventRow.Name.ToString();
                    iconId = eventRow.Icon;
                    var questRowId = eventRow.Quest.RowId;
                    questId = questRowId > ushort.MaxValue ? questRowId - 65536u : questRowId;
                }
                else if (itemSheet.TryGetRow(id, out var itemRow))
                {
                    name = itemRow.Name.ToString();
                    iconId = itemRow.Icon;
                }
                else
                {
                    name = $"Unknown item {item.ItemId}";
                    iconId = 0;
                }

                if (string.IsNullOrWhiteSpace(name)) name = $"Unnamed item {item.ItemId}";
                var entry = new KeyItemEntry(item.InventorySlot, item.ItemId, name, iconId, item.ContainerType, questId);
                allEntries.Add(entry);

                var explicitlyWhitelisted = configuration.WhitelistedItemIds.Contains(item.ItemId);
                if (!explicitlyWhitelisted
                    && eventItemSheet.TryGetRow(id, out var filterEventRow)
                    && filterEventRow.Action.RowId == 0)
                {
                    continue;
                }

                if (configuration.IsBlacklisted(item.ItemId, name)) continue;
                if (!IsQuestAllowedByZone(questId, item.ItemId == WondrousTailsItemId)) continue;
                entries.Add(entry);
            }

            allEntries.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            entries.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            MaintainSelectionLock();
            UpdateAutomaticSelection();
            lastScan = DateTime.Now;
            scanStatus = $"Refreshed {lastScan:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            lastScan = DateTime.Now;
            scanStatus = "Scan failed; see plugin log.";
            Plugin.Log.Error(ex, "Failed to scan Key Items inventory.");
        }
    }
}
