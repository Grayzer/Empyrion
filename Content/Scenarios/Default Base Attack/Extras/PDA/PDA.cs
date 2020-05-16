using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using System;

using Eleon.PdaScript;
using Eleon.Pda;
using Eleon.ModBridge;
using Eleon.Modding;


public class MyBaseAttack : PdaScript {
    public const int cTimeAllowedToReplaceCore = 210;
    string startingChapterName = "";

    public MyBaseAttack() {
        Data.Creator = "Eleon Game Studios [перевод Mungus]";
        Data.CompletedMessage = "15|Когда солнце садится за горизонт, кровь мерцает на угасающем солнце, вы смотрите на своих братьев... без слов, это слишком хорошо понято: это только начало...";
        Data.FailedMessage = "15;t|Среди стонов и криков вы смотрите на небо и делаете последний вздох, солдат Зиракс подходит к вам и прикладывает свой бластер к вашему лицу...";
    }

    // override to check for conditions to start the script and select the starting chapter name
    public override bool CheckActivationConditions() {
        AddChapter(new ChapterZirax(ModManager.ModApi.Playfield.PlanetType));
        startingChapterName = ModManager.ModApi.Playfield.PlanetType;

        return true;
    }

    // override to actually start the chapter
    protected override void RunScenario() {
        StartChapter(startingChapterName);
    }
}

// Chapter: ZIRAX BA ATTACK
public class ChapterZirax : Chapter {
    public IEntity playerBase;
    public IContainer playerBaseAmmoMag;

    int minPlayerCount = 2;
    bool canEnd;
    int waveCompletedCount = 0;

    public Vector3 playerBaseDropPosition;

    public ChapterZirax(string _id) : base(_id) {
        chapterData.ChapterTitle = "Вторжение Зиракс";
        chapterData.Category = ChapterCategory.UCHMission;
        chapterData.Description = "[ffff00]Приготовьтесь к вторжению Зиракс![-]\n\nВы находитесь в изолированном форпосте в далеком мире. У вас есть отчеты " +
                                  "о скором вторжении Зиракс. Вы должны защищать заставу любой ценой.\n\n[ffff00]Найдите время, чтобы ознакомиться с этой базой[-].\n\n" +
                                  "[ff0000]Вы не поставлена задача добычи полезных ископаемых или разработке какой-либо из вашего оборудования. Расходные материалы, оружие, боеприпасы и многое другое будут сброшены на посадочной площадке.[-]";

        chapterData.NoSkip = true;
        chapterData.PlayerLevel = 1;
        chapterData.CompletedMessage = "";
        chapterData.StartDelay = 20;

        runNextChapterOnCompletion = false;
        showBriefingWindow = true;

        canBegin += CheckBegin; // remember to manually set the activeTask and activeTaskId when testing for begin condition
        canComplete += (Chapter chapter) => { return canEnd; };

        AddTask(new InitialPrep("initPrep", (Task sender) => { RunWaves(); }));

        // Add the episodic tasks
        RetakeBaseTask rbt = new RetakeBaseTask("retakeBase", MyBaseAttack.cTimeAllowedToReplaceCore);
        AddTask(rbt, bSequential: false);

        // these are dummies for persistence
        AddTask(new WavePrepTask("prepTask", (Task task) => { StartNextWaveSequence(); }), bSequential: false);

        // include the RetakeBaseTask for later use
        WaveDefendTask wdt = new WaveDefendTask("defendTask", (Task sender) => { StartNextPrepSequence(); }, 1);
        wdt.SetReconquerTask(rbt);
        AddTask(wdt, bSequential: false);

        activeTask = GetTask("initPrep");
        activeTaskId = activeTask.Id;
    }

    bool CheckBegin(Chapter chapter) {
        return GetCurrentPlayerCount() < 0 || GetCurrentPlayerCount() >= minPlayerCount;
    }

    void RunWaves() {
        ModManager.ModApi.PDA.CreateTimer("runWavesTimer", 1f, () => { StartNextPrepSequence(); }, bImmediate: true);
    }

    void StartNextPrepSequence() {
        if (activeTask != null) {
            activeTask.Deactivate();
        }

        WavePrepTask wpt = GetTask("prepTask") as WavePrepTask;
        RemoveTask(wpt);

        wpt = new WavePrepTask("prepTask", (Task task) => { StartNextWaveSequence(); });

        AddTask(wpt, bSequential: false);

        activeTask = wpt;
        activeTaskId = activeTask.Id;

        UpdateChapter();

        // wait  for the network players to get the data
        ModManager.ModApi.PDA.CreateTimer("startPrep", 0.1f, () => { activeTask.Activate(); }, bImmediate: true);
    }

    void StartNextWaveSequence() {
        if (activeTask != null) {
            activeTask.Deactivate();
        }

        WaveDefendTask wdt = GetTask("defendTask") as WaveDefendTask;
        RemoveTask(wdt);

        wdt = new WaveDefendTask("defendTask", (Task sender) => {
            wdt.GenerateResupply();
            StartNextPrepSequence();
        }, ++waveCompletedCount);

        AddTask(wdt, bSequential: false);

        RetakeBaseTask rtb = GetTask("retakeBase") as RetakeBaseTask;
        wdt.SetReconquerTask(GetTask("retakeBase") as RetakeBaseTask);
        activeTask = wdt;
        activeTaskId = activeTask.Id;

        UpdateChapter();

        // short wait
        ModManager.ModApi.PDA.CreateTimer("startWave", 0.1f, () => { activeTask.Activate(); }, bImmediate: true);
    }

    public Vector3 GetDropBoxLandingPadPosition() {
        float randDist = UnityEngine.Random.RandomRange(3, 8);
        float randRotation = UnityEngine.Random.value * 360;
        return (Quaternion.AngleAxis(randRotation, Vector3.up) * Vector3.forward * randDist) + playerBaseDropPosition;
    }

    protected override void GameEventHandler(GameEventType _eventType, object arg1 = null, object arg2 = null, object arg3 = null, object arg4 = null, object arg5 = null) {
        switch (_eventType) {
            case GameEventType.PlayerDied:
                // for now if single player, it's game over
                chapterStatus = Chapter.ChapterStatus.Failure;
                // we will also only end the game when all the players have been killed.
                canEnd = true;
                break;
            default: break;
        }
    }
}

public class InitialPrep : Task {
    const int waitTime = 120; // (2 mins) to give a chance to look around
    bool completeTask;
    Vector3 ammoCtrWorldPos, o2ContWorldPos;

    public InitialPrep(string _id, System.Action<Task> _completeCallback) : base(_id) {
        taskType = PdaMPTaskType.Team;
        canComplete += (Task task) => { return completeTask; };
        shouldTick = true;
        taskActivated += OnActivate;
        taskComplete += _completeCallback;
        //playerJoined += OnPlayerConnect;

        data.TaskTitle = "Добро пожаловать и подготовка";
        data.StartMessage = "15|[ffff00]Были предоставлены маркеры карты для важных контейнеров.[-]\n\nОбратите внимание на эти места и используйте знаки в любое время.";
        data.StartDelay = 1;
        state.Id = PdaManagerBridge.CreateId(_id);

        // wait for players to join
        ActionData startData = new ActionData();
        startData.ActionTitle = "В ожидании игроков: ";
        startData.Description = "Подготовка к входящему вторжению Зиракс.";
        startData.CompletedMessage = "";
        startData.SetTimer = waitTime;
        startData.IncrementCounter = false;

        ActionState startState = new ActionState(startData, state.Id, "waitStart");
        TimerAction startAction = new TimerAction(startData, startState, "waitStart");
        startAction.timerComplete += (TimerAction sender) => { ModManager.ModApi.PDA.CreateTimer("finishTask", 0.5f, () => { completeTask = true; }, bImmediate: true); };
        AddAction(startAction);

        CommitActions();
    }


    void OnActivate(Task sender) {
        // set the player base
        IEntity playerBase = ModManager.ModApi.Playfield.Entities[ModManager.ModApi.PDA.GetPoiEntityId("DN_BaseAttack")];

        // other initialization
        Vector3 ammoCtrLocPos = ModManager.ModApi.PDA.GetBlockLocation("DN_BaseAttack", "Ammo Controller", out ammoCtrWorldPos);

        Vector3 fuelO2LocPos = ModManager.ModApi.PDA.GetBlockLocation("DN_BaseAttack", "Fuel and O2", out o2ContWorldPos);
        IContainer fuelO2 = playerBase.Structure.GetDevice<IContainer>(fuelO2LocPos);
        if (fuelO2 != null) {
            fuelO2.AddItems(PdaBridge.GetTypeFromItemName("OxygenBottleLarge"), 10);
            fuelO2.AddItems(PdaBridge.GetTypeFromItemName("OxygenBottleSmall"), 40);
            fuelO2.AddItems(PdaBridge.GetTypeFromItemName("FusionCell"), 5);
            fuelO2.AddItems(PdaBridge.GetTypeFromItemName("EnergyCell"), 25);
        }

        ModManager.ModApi.PDA.SetMapMarker(true, ammoCtrWorldPos, "Ammo Controller", 100);
        ModManager.ModApi.PDA.SetMapMarker(true, o2ContWorldPos, "Fuel and O2", 100);


        // set the Base's faction to the player's faction
        foreach (IPlayer player in ModManager.ModApi.Playfield.Players.Values) {
            playerBase.Structure.SetFaction(FactionGroup.Player, player.Id);
            break;
        }

        ModManager.ModApi.PDA.GetBlockLocation(ModManager.ModApi.PDA.GetPoiEntityId("DN_BaseAttack"), "DropMarker", out (parentChapter as ChapterZirax).playerBaseDropPosition);

        ModManager.ModApi.PDA.CreateTimer("dropMeds", waitTime / 2, () => {
            ModManager.ModApi.PDA.ShowPdaMessage("An air drop has just been delivered to the landing pad with food and meds.\n\nLocate and retrieve the contents.", 10f, cleanupFirst: true);

            // create and add a reward
            RewardData foodAndMeds = new RewardData() {
                Type = RewardType.DropBox,
                DropBox = new string[] { "PowerBar:30", "Medikit01:50" }
            };

            ModManager.ModApi.PDA.SpawnDropBox(foodAndMeds, (parentChapter as ChapterZirax).GetDropBoxLandingPadPosition(), 30);

        }, bImmediate: true);
    }
}

////////// PREPARATION //////////
public class WavePrepTask : Task {
    const int prepTime = 120; // (2 mins) just enough time to decide a quick plan and loot drones
    bool readyEnd;

    public WavePrepTask(string _id, System.Action<Task> _completeCallback) : base(_id) {
        taskType = PdaMPTaskType.Team;
        shouldTick = true;
        canComplete = (Task task) => { return readyEnd; };
        taskComplete += _completeCallback;

        // set up task data
        data.TaskTitle = "Подготовка к атаке";
        data.StartMessage = string.Format("10;t|Ударная команда, готовься к атаке! ETA в T-{0}.[-]\n\nЗИРАКС АТАКУЕТ НЕМЕДЛЕННО, ЭТО НЕ БУР!\n\n" +
                                          "[b]Я ПОВТОРЯЮ: ЭТО [i]НЕ[/i] БУР![/b]", prepTime);
        data.StartDelay = 3;

        // update the task state id
        state.Id = PdaManagerBridge.CreateId(_id);

        // create and add actions
        ActionData setTimerActionData = new ActionData();
        setTimerActionData.ActionTitle = "Входящий ETA: ";
        setTimerActionData.Description = "Размещайте оборону и готовьтесь к атаке.";
        setTimerActionData.CompletedMessage = "7|Хорошо, дамы, вот  и все. Это нужно будет  сделать.";
        setTimerActionData.AllowManualCompletion = false;
        setTimerActionData.SetTimer = prepTime; // in seconds
        setTimerActionData.IncrementCounter = false;

        ActionState setTimerActionState = new ActionState(setTimerActionData, state.Id, "zx_prep_1_act");
        TimerAction action1 = new TimerAction(setTimerActionData, setTimerActionState, "zx_prep_1_act");
        action1.timerComplete += (TimerAction sender) => { readyEnd = true; };
        AddAction(action1);

        CommitActions();
    }
}

//////// DEFEND ///////////////////////
public class DefendTask : Task {
    const string cNpcCoreBlockName = "CoreNPC";
    const string cPlayerCoreBlockName = "Core";

    bool npcCorePlaced, waveDestroyed;
    RetakeBaseTask reconquerTask;

    public DefendTask(string _id) : base(_id) {
        taskComplete += TaskCompleted;
    }

    void TaskCompleted(Task task) {
        MarkTaskComplete();
    }

    protected override void GameEventHandler(GameEventType _eventType, object _arg1 = null, object _arg2 = null, object _arg3 = null, object _arg4 = null, object _arg5 = null) {
        switch (_eventType) {
            case GameEventType.NpcCorePlaced:
                OnNpcCorePlaced();
                return; // don't call the internal handler
            default: break;
        }
    }

    public void SetReconquerTask(RetakeBaseTask _reconquerTask) {
        reconquerTask = _reconquerTask;
    }

    void OnNpcCorePlaced() {
        //UnityEngine.Debug.Log("*** DefendWaveTask.OnNpcCorPlaced()");
        if (npcCorePlaced) {
            return;
        }

        npcCorePlaced = true;
        Deactivate(bSelf: true);
        reconquerTask.Activate(this);
    }

    // override reactivate to:
    // 1. Check if the wave has been destroyed in the meantime and if so, advance
    // 2. if not, reset the status of the npc core
    public override void Reactivate() {
        if (reconquerTask.bWaveDestroyed) {
            // We can manually flag this task as completed if it happened during the reclaiming of the base
            MarkTaskComplete();
        } else {
            // the wave is still fighting, reactivate
            //UnityEngine.Debug.Log("*** DefendWaveTask.Reactivate(): still fighting.");
            npcCorePlaced = false;
            base.Reactivate();
        }
    }
}

public class WaveDefendTask : DefendTask {
    System.Random rand;
    const int maxScenVal = 3;
    int waveNumber;

    const string taskTitleFmtStr = "[b][ff0000]ОТБИТА ВОЛНА: {0}[-][/b]";

    public WaveDefendTask(string _id, System.Action<Task> _onTaskComplete, int _waveNumber = 1) : base(_id) {
        rand = new System.Random();

        taskComplete = _onTaskComplete;
        taskType = PdaMPTaskType.Team;

        waveNumber = _waveNumber;

        // setup the task data
        data.TaskTitle = string.Format(taskTitleFmtStr, waveNumber); //
        data.StartMessage = "10;t|[ffff00]Ударная команда, занимай позиции![-]";
        data.CompletedMessage = "10|Это последняя из них.\n\nВнимание, Орбитальное командование посылает  [ffff00]контейнеры для пополнения запасов[-].\n\nЗаберите их [ff0000]и возвращайтесь назад![-]";
        data.StartDelay = 2;

        // set the task state id
        state.Id = PdaManagerBridge.CreateId(_id);

        // create and add actions
        GenerateAttackWaves();
        CommitActions();
    }

    int CalculateWaveCost() {
        const float amp = 4f;
        const float scale = 10.0f;

        int result = Mathf.FloorToInt((float)((Math.Log(amp*waveNumber*waveNumber) + 1) * scale));
        return result;
    }

    string SelectScenario() {
        int fac = Mathf.FloorToInt(Mathf.Clamp((float)(waveNumber/maxScenVal), 0, 3));
        int dig = waveNumber % maxScenVal;

        return string.Format("BALevel{0}{1}", fac/10 < 1 ? "" : (fac/10).ToString(), dig);
    }

    void OnWaveActionActivate(Eleon.PdaScript.Action action) {
        // this will be set either from the constructor, or from the save file.
        WaveAttackAction wa = (WaveAttackAction)action;
        waveNumber = wa.waveNumber;

        data.TaskTitle = string.Format(taskTitleFmtStr, waveNumber); //
    }

    void GenerateAttackWaves() {
        int attackWaveCount = waveNumber/3;
        attackWaveCount = attackWaveCount < 1 ? 1 : attackWaveCount;

        for (int i = 0;i<attackWaveCount;i++) {
            ActionData attackWaveAction = new ActionData();
            attackWaveAction.ActionTitle = "===";
            attackWaveAction.Description = "===";
            attackWaveAction.AllowManualCompletion = false;

            attackWaveAction.WaveStart = new WaveStartData() {
                Name = SelectScenario(),
                Faction = "Zirax",
                Cost = CalculateWaveCost(),
                Target = "DN_BaseAttack"
            };

            ActionState attackWaveActionState = new ActionState(attackWaveAction, state.Id, "zx_wave_" + i);

            // spawn a wave manually, not using an action, just tracking with the state
            WaveAttackAction waveAction = new WaveAttackAction(attackWaveAction, attackWaveActionState, "zx_wave_" + i, waveNumber, _coreTimerTime: 210);
            waveAction.actionActivated += OnWaveActionActivate;
            waveAction.IsHUDDisplay = false;

            AddAction(waveAction);
        }
    }

    public void GenerateResupply() {
        List<string> items = new List<string>() {
            string.Format("30mmBullet:{0}", 100 * waveNumber),
            string.Format("5.8mmBullet:{0}", 100 * waveNumber),
            string.Format("EnergyCell:{0}",  2),
            string.Format("FlakRocket:{0}",  7 * waveNumber),
            //string.Format("SlowRocket:{0}", 10 * ModManager.ModApi.Playfield.Players.Count),
        };

        if (waveNumber % 2 == 0) {
            items.Add(string.Format("HullArmoredLargeBlocks:{0}", UnityEngine.Random.Range(3, 6).ToString()));
            items.Add(string.Format("PowerBar:{0}", 3 * ModManager.ModApi.Playfield.Players.Count));
            items.Add("SentryGunBlocks: 1");
        }

        // create and add a reward
        RewardData reward = new RewardData() {
            Type = RewardType.DropBox,
            DropBox = items.ToArray()
        };

        ModManager.ModApi.PDA.SpawnDropBox(reward, (parentChapter as ChapterZirax).GetDropBoxLandingPadPosition(), 30);
    }
}

// a base class to make base recapture scripts with
public class RetakeBaseTask : Task {
    Task caller = null;

    public bool bWaveDestroyed;
    const string cNpcCoreBlockName = "CoreNPC";
    const string cPlayerCoreBlockName = "Core";

    string completedMessageSuccess = "10;t|Это было слишком близко, девочки, если вы позволите этому случиться снова, вам придется ответить мне!";
    string completedMessageFail = "[ffff00]Зиракс захватили вашу базу! Миссия[-] [ff0000]ПРОВАЛЕНА![-]";

    public RetakeBaseTask(string _id, int _retakeTime) : base(_id) {
        // set up task data
        data.TaskTitle = "Восстановите базу!";
        data.StartMessage = "10;t|Мы должны восстановить нашу базу - иначе все кончено! Пошевеливайтесь!";
        data.StartDelay = 1;

        // update the task state id
        state.Id = PdaManagerBridge.CreateId(_id);

        // create and add actions
        ActionData setTimerActionData = new ActionData();
        setTimerActionData.ActionTitle = "Time Remaining: ";
        setTimerActionData.Description = "===";
        setTimerActionData.AllowManualCompletion = false;
        setTimerActionData.SetTimer = _retakeTime;
        setTimerActionData.IncrementCounter = false;

        ActionState setTimerState = new ActionState(setTimerActionData, state.Id, "reclaim_1");
        TimerAction ta = new TimerAction(setTimerActionData, setTimerState, "reclaim_1");
        ta.timerComplete += OnTimerComplete;

        AddAction(ta);

        // always call this last
        CommitActions();
    }

    public void Activate(Task _caller) {
        caller = _caller;

        // spawn a drop box with a core to ensure one is available for replacement
        RewardData cores = new RewardData() {
            Type = RewardType.DropBox,
            DropBox = new string[2] { "Core:2", "Explosives:5" }
        };
        ModManager.ModApi.PDA.SpawnDropBox(cores, (parentChapter as ChapterZirax).GetDropBoxLandingPadPosition(), 30);

        base.Activate();
    }

    public void OnBaseReclaimed() {
        TimerAction timerAction = GetAction("reclaim_1") as TimerAction;
        if (timerAction != null) {
            timerAction.data.CompletedMessage = completedMessageSuccess;
            timerAction.StopAndResetTimer();
        }

        if (caller != null) {
            caller.Reactivate();
        }

        Deactivate(true);
    }

    public void OnTimerComplete(TimerAction timerAction) {
        if (timerAction != null) {
            // end the game and force players to quit
            ModManager.ModApi.PDA.ShowPdaDialog(completedMessageFail, ModApiDialogButtons.Quit);
        }
    }

    protected override void GameEventHandler(GameEventType _eventType, object _arg1 = null, object _arg2 = null, object _arg3 = null, object _arg4 = null, object _arg5 = null) {
        switch (_eventType) {
            case GameEventType.BlockChanged:
                string newBlockName = ModManager.ModApi.PDA.GetBlockName(_arg4);
                string oldBlockName = ModManager.ModApi.PDA.GetBlockName(_arg3);

                //UnityEngine.Debug.LogFormat("PDAReconquerBase: Block changed: old={0}, new={1}", oldBlockName, newBlockName);
                if (oldBlockName != cPlayerCoreBlockName && newBlockName == cPlayerCoreBlockName) {
                    OnBaseReclaimed();
                }
                break;
            case GameEventType.WaveDestroyed:
                uint id = (uint)_arg2;
                //UnityEngine.Debug.LogFormat("*** [SCRIPT] Wave destroyed. id={0}", id);
                //if (id == attackId) {
                //    UnityEngine.Debug.LogFormat("*** [SCRIPT] Wave destroyed. attackId={0}", attackId);
                bWaveDestroyed = true;
                //}
                break;
            default: break;
        }

        // call any additional handlers for other events
        base.GameEventHandler(_eventType, _arg1, _arg2, _arg3, _arg4, _arg5);
    }
}
