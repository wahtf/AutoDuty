using AutoDuty.Helpers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;

namespace AutoDuty.Managers
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using static Data.Classes;
    using Screens = CrucibleUi.Screens;

    public class CrucibleManager(TaskManager _taskManager)
    {
        internal const uint LaudaTerritory = 148;
        internal const uint LaudaDataId    = 1059316;

        private static readonly Vector3 LaudaPosition = new(25.50f, -6.00f, 67.52f);

        private static readonly Dictionary<uint, uint> BoardByDuty = new()
                                                                     {
                                                                         [1088] = 1,
                                                                         [1089] = 2,
                                                                         [1090] = 3,
                                                                         [1091] = 4,
                                                                         [1092] = 5
                                                                     };

        private readonly CrucibleTeamSetup teamSetup = new();
        private readonly CrucibleMenus     menus     = new();

        private bool onBoard;
        private int  boardStep;

        internal string Status => this.menus.Status;

        internal static bool IsCrucibleTerritory(uint territoryType) =>
            ContentHelper.DictionaryContent.TryGetValue(territoryType, out Content? content) && content.DutyModes.HasFlag(DutyMode.Crucible);

        internal static void GotoLauda() =>
            GotoHelper.Invoke(LaudaTerritory, [LaudaPosition], LaudaDataId, 0.25f, 4f, false, false, true);

        internal unsafe void RegisterCrucible(Content content)
        {
            if (!BoardByDuty.TryGetValue(content.ContentFinderCondition, out uint board))
            {
                _taskManager.Enqueue(() => Svc.Log.Error($"No Crucible board for {content.Name} (CFC {content.ContentFinderCondition})"), "RegisterCrucible");
                return;
            }

            _taskManager.Enqueue(() => Svc.Log.Info($"Queueing Crucible: {content.Name} (board {board})"), "RegisterCrucible");
            _taskManager.Enqueue(() => Plugin.action = $"Queueing Crucible: {content.Name}",              "RegisterCrucible");

            if (!PlayerHelper.IsValid)
            {
                _taskManager.Enqueue(() => PlayerHelper.IsValid, "RegisterCrucible", new TaskManagerConfiguration(int.MaxValue));
                _taskManager.EnqueueDelay(2000);
            }

            _taskManager.Enqueue(() => this.boardStep = 0, "RegisterCrucible-OpenBoard");
            _taskManager.Enqueue(() => this.OpenBoard(board), "RegisterCrucible-OpenBoard", new TaskManagerConfiguration(30000));

            _taskManager.Enqueue(() => this.teamSetup.Start(AutoDuty.Configuration.Meta.Crucible.TeamMode), "RegisterCrucible-Team");
            _taskManager.Enqueue(() =>
                                 {
                                     bool done = this.teamSetup.Update();
                                     Plugin.action = this.teamSetup.Status;
                                     return done;
                                 }, "RegisterCrucible-Team", new TaskManagerConfiguration(300000));
            _taskManager.Enqueue(() =>
                                 {
                                     if (this.teamSetup.Error == null)
                                         return;
                                     Svc.Log.Error($"Crucible team setup failed: {this.teamSetup.Error}");
                                     _taskManager.Abort();
                                     Plugin.Stage = Stage.Stopped;
                                 }, "RegisterCrucible-TeamCheck");

            AtkUnitBase* addon = null;
            _taskManager.Enqueue(() => GenericHelpers.TryGetAddonByName(CrucibleUi.BoardLayout, out addon) && GenericHelpers.IsAddonReady(addon), "RegisterCrucible-Challenge");
            _taskManager.Enqueue(() =>
                                 {
                                     Screens.StageDetail.Confirm(addon);
                                     this.challengedAt = DateTime.UtcNow;
                                 }, "RegisterCrucible-Challenge");
            _taskManager.Enqueue(this.Commence, "RegisterCrucible-Commence");
        }

        private DateTime challengedAt;

        private unsafe bool Commence()
        {
            if (CrucibleUi.TryReady("ContentsFinderConfirm", out AtkUnitBase* confirm))
            {
                AddonHelper.FireCallBack(confirm, true, 8);
                return true;
            }

            if (DateTime.UtcNow - this.challengedAt <= TimeSpan.FromSeconds(5) && CrucibleUi.TryReady(CrucibleUi.YesNo, out AtkUnitBase* smallTeam))
            {
                Svc.Log.Info("[Crucible] Team is smaller than ten; confirming the challenge anyway");
                Screens.Prompt.Yes(smallTeam);
                this.challengedAt = DateTime.MinValue;
            }

            return false;
        }

        private static string? challengeText;

        private static string ChallengeText =>
            challengeText ??= Svc.Data.GetExcelSheet<RawRow>(name: "custom/009/CtsXbmEntrance_00976").TryGetRow(1, out RawRow row)
                                  ? row.ReadStringColumn(1).ExtractText().TrimEnd('.', '。', ' ')
                                  : "";

        private static unsafe void ChooseChallenge(AtkUnitBase* menu)
        {
            int index = ChallengeText.Length > 0 ? CrucibleUi.SelectStringIndex(menu, ChallengeText) : -1;
            if (index < 0)
            {
                Svc.Log.Warning($"[Crucible] Lauda's menu has no \"{ChallengeText}\" option");
                return;
            }

            AddonHelper.FireCallBack(menu, true, index);
        }

        private unsafe bool OpenBoard(uint board)
        {
            if (CrucibleUi.IsOpen(CrucibleUi.TeamWindow))
                return true;

            if (!EzThrottler.Throttle("CrucibleOpenBoard", 300))
                return false;

            if (CrucibleUi.TryReady(CrucibleUi.BoardList, out AtkUnitBase* list))
            {
                if (this.boardStep == 0)
                {
                    Screens.StageList.Highlight(list, board);
                    this.boardStep = 1;
                }
                else
                {
                    Screens.StageList.Open(list, board);
                    this.boardStep = 0;
                    EzThrottler.Throttle("CrucibleOpenBoard", 1000, true);
                }

                return false;
            }

            if (CrucibleUi.TryReady("SelectString", out AtkUnitBase* menu))
            {
                ChooseChallenge(menu);
                EzThrottler.Throttle("CrucibleOpenBoard", 600, true);
                return false;
            }

            if (GenericHelpers.TryGetAddonByName("Talk", out AtkUnitBase* talk) && GenericHelpers.IsAddonReady(talk))
            {
                AddonHelper.ClickTalk();
                return false;
            }

            if (!PlayerHelper.IsReady)
                return false;

            if (ObjectHelper.GetObjectByDataId(LaudaDataId) is not { IsTargetable: true } lauda)
                return false;

            ObjectHelper.InteractWithObject(lauda, false);
            EzThrottler.Throttle("CrucibleOpenBoard", 2000, true);
            return false;
        }

        internal void Update()
        {
            if (!Player.Available)
                return;

            CrucibleTeam.UpdateCache();

            bool running = (Plugin.States.HasFlag(PluginState.Looping) || Plugin.States.HasFlag(PluginState.Navigating)) && !Plugin.States.HasFlag(PluginState.Paused);
            bool bypass  = AutoDuty.Configuration.Meta.Crucible.MenusWithoutRun;
            bool board   = (running || bypass) && IsCrucibleTerritory(Svc.ClientState.TerritoryType);

            if (bypass && !running && !board && _taskManager.NumQueuedTasks == 0)
            {
                this.UpdateManualQueue();
            }
            else
            {
                this.manualStep  = ManualStep.Idle;
                this.manualAwake = true;
            }

            if (!board)
            {
                if (this.onBoard)
                    this.menus.Reset();
                this.onBoard = false;
                return;
            }

            this.onBoard = true;
            this.menus.Update();
        }

        private enum ManualStep
        {
            Idle,
            Team,
            Challenge,
            Commence,
            Finished
        }

        private static readonly string[] ManualWakeWindows = [CrucibleUi.TeamWindow, CrucibleUi.BoardList, "SelectString", "Talk"];

        private ManualStep manualStep;
        private DateTime   manualSince;
        private bool       manualAwake = true;

        internal void Watch()
        {
            CrucibleTeam.Watch();
            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, ManualWakeWindows, this.OnManualWindowOpened);
        }

        internal void Unwatch()
        {
            CrucibleTeam.Unwatch();
            Svc.AddonLifecycle.UnregisterListener(this.OnManualWindowOpened);
        }

        private void OnManualWindowOpened(AddonEvent type, AddonArgs args) =>
            this.manualAwake = true;

        private unsafe void UpdateManualQueue()
        {
            DateTime now = DateTime.UtcNow;

            switch (this.manualStep)
            {
                case ManualStep.Idle:
                    if (!this.manualAwake || !EzThrottler.Throttle("CrucibleManualQueue", 100))
                        return;

                    if (CrucibleUi.IsOpen(CrucibleUi.TeamWindow))
                    {
                        Svc.Log.Info($"[Crucible] Testing: Team Composition opened, setting up a {AutoDuty.Configuration.Meta.Crucible.TeamMode} team");
                        this.teamSetup.Start(AutoDuty.Configuration.Meta.Crucible.TeamMode);
                        this.SetManual(ManualStep.Team, now);
                        return;
                    }

                    if (CrucibleUi.TryReady(CrucibleUi.BoardList, out AtkUnitBase* list))
                    {
                        if (Plugin.CurrentTerritoryContent is { } content && BoardByDuty.TryGetValue(content.ContentFinderCondition, out uint board) &&
                            EzThrottler.Throttle("CrucibleOpenBoard", 600))
                        {
                            if (this.boardStep == 0)
                                Screens.StageList.Highlight(list, board);
                            else
                                Screens.StageList.Open(list, board);
                            this.boardStep = 1 - this.boardStep;
                        }

                        return;
                    }

                    this.boardStep = 0;

                    if (Svc.Targets.Target?.BaseId != LaudaDataId)
                    {
                        this.manualAwake = false;
                        return;
                    }

                    if (CrucibleUi.TryReady("SelectString", out AtkUnitBase* menu))
                    {
                        if (EzThrottler.Throttle("CrucibleOpenBoard", 1000))
                            ChooseChallenge(menu);
                    }
                    else if (GenericHelpers.TryGetAddonByName("Talk", out AtkUnitBase* talk) && GenericHelpers.IsAddonReady(talk))
                    {
                        AddonHelper.ClickTalk();
                    }
                    else
                    {
                        this.manualAwake = false;
                    }

                    return;

                case ManualStep.Team:
                    if (!CrucibleUi.IsOpen(CrucibleUi.TeamWindow) && !CrucibleUi.IsOpen(CrucibleUi.BestiaryWindow))
                    {
                        this.SetManual(ManualStep.Idle, now);
                        return;
                    }

                    if (!this.teamSetup.Update())
                        return;

                    if (this.teamSetup.Error != null)
                    {
                        Svc.Chat.PrintError($"[AutoDuty] Crucible team setup failed: {this.teamSetup.Error}");
                        this.SetManual(ManualStep.Finished, now);
                        return;
                    }

                    this.SetManual(ManualStep.Challenge, now);
                    return;

                case ManualStep.Challenge:
                    if (CrucibleUi.TryReady(CrucibleUi.BoardLayout, out AtkUnitBase* layout))
                    {
                        Screens.StageDetail.Confirm(layout);
                        this.challengedAt = now;
                        this.SetManual(ManualStep.Commence, now);
                    }
                    else if (now - this.manualSince > TimeSpan.FromSeconds(10))
                    {
                        this.SetManual(ManualStep.Finished, now);
                    }

                    return;

                case ManualStep.Commence:
                    if (this.Commence())
                    {
                        this.SetManual(ManualStep.Finished, now);
                    }
                    else if (now - this.manualSince > TimeSpan.FromSeconds(20))
                    {
                        this.SetManual(ManualStep.Finished, now);
                    }

                    return;

                case ManualStep.Finished:
                    if (!CrucibleUi.IsOpen(CrucibleUi.TeamWindow) && !CrucibleUi.IsOpen(CrucibleUi.BoardLayout))
                        this.SetManual(ManualStep.Idle, now);
                    return;
            }
        }

        private void SetManual(ManualStep step, DateTime now)
        {
            if (this.manualStep != step)
                Svc.Log.Debug($"[Crucible] Testing queue: {this.manualStep} -> {step}");
            if (step == ManualStep.Idle)
                this.manualAwake = true;
            this.manualStep  = step;
            this.manualSince = now;
        }
    }
}
