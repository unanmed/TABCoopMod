using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Reflection.Emit;
using DXVision;
using DXVision.GUI;
using HarmonyLib;
using ZX;
using ZX.Commands;
using ZX.Components;
using ZX.Entities;
using ZX.GUI;

namespace TABCoopMod.Core
{
    /// <summary>
    /// Strongly-typed bridge to TheyAreBillions.exe and DXVision.dll internals.
    /// Isolates all obfuscated reflection/delegate lookups in one place.
    /// </summary>
    public static class GameBridge
    {
        public static Assembly GameAssembly { get; private set; }
        public static Assembly EngineAssembly { get; private set; }

        // Resolved Obfuscated System Types
        public static Type TypeZXGame { get; private set; }
        public static Type TypeZXSystem { get; private set; }
        public static Type TypeZXSystemGameLevel { get; private set; }
        public static Type TypeZXSystemMainMenu { get; private set; }
        public static Type TypeZXMessageBoxHelper { get; private set; }
        public static Type TypeZXWindowMessage { get; private set; }
        public static Type TypeZXSteamManager { get; private set; }
        public static Type TypeZXSystemAudio { get; private set; }

        // Cached Reflection Handles
        private static MethodInfo _mZXGameCurrent;
        private static MethodInfo _mLevelSysCurrent;
        private static MethodInfo _mMainMenuCurrent;
        private static MethodInfo _mGameStateCurrent;
        private static MethodInfo _mLevelStateCurrent;
        private static MethodInfo _methodLevelStateAvailFood;
        private static MethodInfo _methodLevelStateAvailWorkers;
        private static MethodInfo _methodLevelStateAvailEnergy;

        private static MethodInfo _mLocalize;
        private static MethodInfo _mGetSavesDir;
        private static MethodInfo _methodWriteSaveFile;
        private static MethodInfo _methodSaveGameFlow;
        private static MethodInfo _methodLoadGameAndEnter;
        private static MethodInfo _methodShowTopBanner;
        private static MethodInfo _methodShowGroundIndicator;
        private static MethodInfo _methodLevelSysOpenGameMenu;
        private static MethodInfo _methodAssignGroupMove;
        private static MethodInfo _methodPlaceBuilding;

        private static MethodInfo _mCmdById;
        private static MethodInfo _mCmdByName;
        private static MethodInfo _mCmdExec;
        private static MethodInfo _methodCanExecuteCmd;
        private static MethodInfo _methodIsVisibleCmd;
        private static MethodInfo _methodGetResourcesCost;
        private static Type _typeCreateEntityCommand;
        public static Type TypeStructure { get; private set; }
        public static Type TypeColonist { get; private set; }
        public static Type TypeBuilding { get; private set; }
        private static Type _typeEntityBonusCreation;
        private static Type _typeExtractFromSlot;
        private static Type _typeContractMercenary;
        public static Type TypeTheInn { get; private set; }
        private static PropertyInfo _propBonusCmdNSlot;
        private static PropertyInfo _propBonusCmdTemplate;
        private static PropertyInfo _propBonusCmdEntity;
        private static PropertyInfo _propExtractCmdNSlot;
        private static PropertyInfo _propMercCmdNSlot;
        private static PropertyInfo _propMercCmdTemplate;
        private static PropertyInfo _propMercCmdEntity;
        private static MethodInfo _mTheInnGetMercenaries;
        private static PropertyInfo _propCmdableAvailableCmds;
        private static FieldInfo _fieldZXGameAllCommands;
        private static MethodInfo _methodLevelStateConsumeBonus;
        private static MethodInfo _methodLevelSysClearPendingMode;
        private static MethodInfo _methodGetTemplateFromEntityType;
        private static MethodInfo _methodEntityCanPlaceAtCell;
        private static MethodInfo _methodEntityUpdateCellPosition;

        private static MethodInfo _mGetEntityCmd;
        private static MethodInfo _mGetEntitySel;
        private static MethodInfo _mGetEntityLife;
        private static MethodInfo _mGetEntityBehaviour;
        private static MethodInfo _mGetEntityMovable;
        private static MethodInfo _methodEntityGetDefaultParams;
        private static Type _typeUnit;

        private static FieldInfo _fieldAudioCurrent;
        private static MethodInfo _methodAudioPickWeightedVoice;
        private static MethodInfo _methodAudioPlayUniqueList;
        private static MethodInfo _methodAudioPlay2DSoundList;
        private static PropertyInfo _propDefParamsSoundsGeneric;
        private static PropertyInfo _propDefParamsSoundsAttack;
        private static PropertyInfo _propDefParamsSoundsCreation;
        private static PropertyInfo _propDefParamsSoundsSelected;

        private static Type _typeZombie;
        private static PropertyInfo _propZombieAttackCC;
        private static PropertyInfo _propMovableMode;
        private static PropertyInfo _propCBehavBehaviour;
        private static PropertyInfo _propBehavData;
        private static FieldInfo _fieldBDataTargetRef;
        private static PropertyInfo _propTargetRefId;
        private static PropertyInfo _propBDataLastEnemy;
        private static FieldInfo _fieldMovableTargetPos;
        private static bool _zombieAggroReflectionInit;

        // 12. Random Survival Map Generator & Balance Scaling Handles
        public static Type TypeZXMapGenerator { get; private set; }
        public static Type TypeCommandCenter { get; private set; }
        public static Type TypeRanger { get; private set; }
        public static Type TypeSoldierRegular { get; private set; }
        private static Type _typeZXRandomLevelParams;
        private static Type _typeZXMapTheme;
        private static Type _typeZXLevelExtension;
        private static Type _typeZXMapDrawer;
        private static Type _typeZXLevelEvent;
        private static MethodInfo _methodMapGeneratorGenerate;
        private static MethodInfo _methodZXMapThemeGetThemes;
        private static MethodInfo _methodLevelEventScalePop;
        private static MethodInfo _methodLevelSysCapturePanorama;
        private static MethodInfo _methodLevelSysEnterGameView;
        private static MethodInfo _mInitLevel;
        private static PropertyInfo _propLevelStateEndStatus;
        private static FieldInfo _fieldLevelSysGameEnded;
        private static FieldInfo _fieldLevelSysScreenAnim;
        private static MethodInfo _methodLevelSysTriggerGameEnd;
        private static MethodInfo _methodLevelSysShowSurvivalEnd;
        private static MethodInfo _methodLevelSysShowCampaignEnd;
        private static MethodInfo _methodGameShowMayorSelection;
        private static Type _typeZXGameState;
        private static MethodInfo _methodLevelSysRefreshSelectionGroupsHud;
        private static MethodInfo _methodLevelStateGetSelectionGroups;
        private static Type _typeKeyboardBindingsManager;
        private static MethodInfo _methodKeyboardIsKeyForCommand;
        private static FieldInfo _fieldLevelSysBuildAreaCursor;
        private static Type _typeOCellAreaCursor;
        private static FieldInfo _fieldCursorLastEnergyCell;
        private static MethodInfo _methodCursorClearEnergyPreview;
        private static MethodInfo _methodCursorUpdateVisual;
        private static Type _typeZXOrder;
        private static ConstructorInfo _ctorZXOrder;
        private static Type _typeCBuildable;
        private static PropertyInfo _propCBuildableFactor;
        private static PropertyInfo _propCBuildableMode;
        private static PropertyInfo _propStructureIsBeingBuilt;
        private static MethodInfo _methodBuildingRecalcResources;
        private static MethodInfo _methodStructureUpdateEnabled;
        private static MethodInfo _methodLevelStateRecordEntityCreated;
        private static MethodInfo _methodLevelSysRefreshCmdPanel;
        private static PropertyInfo _propEntityTeam;
        private static PropertyInfo _propEntityRelation;
        private static PropertyInfo _propMovableTargetPosProp;
        private static PropertyInfo _propMovablePath;
        private static FieldInfo _fieldMovablePendingPath;
        private static PropertyInfo _propRandomParamsNCells;
        private static PropertyInfo _propRandomParamsFactorDuration;
        private static PropertyInfo _propRandomParamsFactorZombiePop;
        private static PropertyInfo _propMapThemeDoomVillagesSize;
        private static PropertyInfo _propMapThemeNumTreasures;
        private static PropertyInfo _propMapThemeNumSuppyFood;
        private static PropertyInfo _propMapThemeNumSuppyEnergy;
        private static PropertyInfo _propMapThemeNumInfectedGiants;
        private static PropertyInfo _propMapThemeNumInfectedMutants;
        private static PropertyInfo _propMapThemeInfectedFactor;
        private static PropertyInfo _propMapThemeInfectedDispersion;
        private static PropertyInfo _propMapThemeMinDistWeak;
        private static PropertyInfo _propMapThemeMinDistMed;
        private static PropertyInfo _propMapThemeMinDistStrong;
        private static PropertyInfo _propMapThemeMinDistPowerful;
        private static PropertyInfo _propRandomParamsThemeType;
        private static PropertyInfo _propLevelExtMapDrawer;
        private static PropertyInfo _propLevelExtLevelEvents;
        private static PropertyInfo _propMapDrawerExtraEntities;
        private static PropertyInfo _propMapDrawerLayerTerrain;
        private static PropertyInfo _propMapDrawerLayerObjects;
        private static PropertyInfo _propMapDrawerNCells;
        private static PropertyInfo _propLevelEventStartTimeH;
        private static PropertyInfo _propLevelEventGameTimeH;
        private static PropertyInfo _propLevelEventFinalSwarm;
        private static PropertyInfo _propLevelEventAllInfectedToCC;
        private static PropertyInfo _propLevelStateExtraWorkers;
        private static PropertyInfo _propLevelStateExtraFood;
        private static MethodInfo _methodLevelStateRecalcResources;
        private static PropertyInfo _propGameStateRandom;

        private static MethodInfo _mCurCmd;
        private static MethodInfo _mCancelCur;
        private static MethodInfo _mCancelSlot;

        private static FieldInfo _fieldSelectedList;
        private static MethodInfo _mPrimarySel;
        private static MethodInfo _mUnselectAll;

        private static MethodInfo _methodShowAlert;
        private static MethodInfo _methodShowYesNo;
        private static MethodInfo _methodShowListSelector;
        private static MethodInfo _methodShowInputBox;
        private static MethodInfo _methodGetPlainTextRenderer;
        public static Type TypeDXEditBoxTypeOfInput { get; private set; }
        private static PropertyInfo _propWindowMsgTitle;
        private static PropertyInfo _propWindowMsgText;

        // 13. Phase 4 M1 Multi-Faction Mode Handles
        private static Type _typeZXSystemMapLayers;
        private static FieldInfo _fieldMapLayersCurrent;
        private static FieldInfo _fieldMapLayersEnergyGrid;
        private static FieldInfo _fieldMapLayersEnergyFrontier;
        private static FieldInfo _fieldMapLayersEnergySources;
        private static FieldInfo _fieldMapLayersEnergyLastIdx;
        private static MethodInfo _methodMapLayersRebuildEnergyGrid;
        public static Type TypeCEnergyTransferer { get; private set; }
        public static Type TypeCNeutralJoinable { get; private set; }
        public static Type TypeCLife { get; private set; }
        public static Type TypeTrain { get; private set; }
        public static Type TypeUpgrade { get; private set; }
        private static FieldInfo _fieldLevelStateCachedCC;
        private static MethodInfo _methodLevelStateRefreshCCCell;
        private static PropertyInfo _propLevelStateCurrentCCCell;
        private static PropertyInfo _propLevelStateInfectedDestiny;
        private static PropertyInfo _propLevelStateScriptVars;
        private static FieldInfo _fieldLevelSysCurrentCC;
        private static PropertyInfo _propBuildingInfected;
        private static PropertyInfo _propDefaultParamsEnergyRadius;
        private static Type _typeBData;
        private static MethodInfo _methodBDataGetOwner;
        private static PropertyInfo _propBDataTargetPos;
        private static PropertyInfo _propEntityRefEntity;
        private static PropertyInfo _propEntityRefId;
        private static MethodInfo _methodTerrainSystemGetCurrent;
        private static PropertyInfo _propTerrainSystemCells;

        // 14. Phase 4 M2 & M3 Multi-Faction Economy, Tech, Wonders & Visuals
        public static Type TypeTechnology { get; private set; }
        public static Type TypeCInvisibleWhenNotWatched { get; private set; }
        private static MethodInfo _methodCInvisibleUpdateOpacity;
        private static PropertyInfo _propLevelExtHideEnemies;
        private static MethodInfo _methodLevelExtGetCurrent;
        public static Type TypeUndoBuilding { get; private set; }
        public static Type TypeDestroy { get; private set; }
        public static Type TypeCResourceGenerator { get; private set; }
        public static Type TypePickableItem { get; private set; }
        public static Type TypeTheGreatTelescope { get; private set; }
        public static Type TypeTheAcademy { get; private set; }
        public static Type TypeTheSpire { get; private set; }
        public static Type TypeWoodWorkshop { get; private set; }
        public static Type TypeStoneWorkshop { get; private set; }
        public static Type TypeFoundry { get; private set; }
        public static Type TypeTheCrystalPalace { get; private set; }
        public static Type TypeTheTransmutator { get; private set; }
        public static Type TypeTheVictorious { get; private set; }
        public static Type TypeTechGreatTelescope { get; private set; }
        public static Type TypeTechSoldierAcademy { get; private set; }
        public static Type TypeTechElectricSpire { get; private set; }
        public static Type TypeZXResourcesCost { get; private set; }
        public static Type TypeZXMayor { get; private set; }

        private static FieldInfo _fLsWood, _fLsStone, _fLsIron, _fLsOil, _fLsGold, _fLsStorage;
        private static FieldInfo _fLsFoodSup, _fLsWorkSup, _fLsEngSup, _fLsFoodNeed, _fLsWorkNeed, _fLsEngNeed;
        private static FieldInfo _fLsColonists, _fLsGoldByCc, _fLsTotalStock;
        private static PropertyInfo _pLsExtraEnergy, _pLsFoodRes, _pLsWorkRes, _pLsEngRes;
        private static PropertyInfo _pLsWoodProd, _pLsStoneProd, _pLsIronProd, _pLsOilProd, _pLsGoldProd;
        private static PropertyInfo _pLsWoodProdRes, _pLsStoneProdRes, _pLsIronProdRes, _pLsOilProdRes;
        private static PropertyInfo _pLsNegGoldTurns, _pLsNegFoodTurns, _pLsNMarkets, _pLsMaxGoldExport, _pLsMaxColonists;
        private static PropertyInfo _pLsArmyGold, _pLsArmyFoodPen, _pLsStructGold, _pLsGoldPerCol;
        private static PropertyInfo _pLsBonusTemplates, _pLsOnlyOneCmds, _pLsShowFullMap, _pLsCurrentCc;
        private static MethodInfo _mLsRecalculate, _mLsSetEnhancerBuildings, _mLsAddWoodMarket, _mLsAddStoneMarket;
        private static MethodInfo _mLsAddIronMarket, _mLsAddOilMarket, _mLsAddGoldDirect, _mLsAddCost, _mLsConsumeCost;
        private static MethodInfo _mLsAddOnlyOne, _mLsHasOnlyOne;
        private static MethodInfo _mLsSellWood, _mLsSellStone, _mLsSellIron, _mLsSellOil;

        private static MethodInfo _mResCostForType;
        private static PropertyInfo _pResGold, _pResWood, _pResStone, _pResIron, _pResOil, _pResFood, _pResWorkers, _pResEnergy;
        private static PropertyInfo _pCcTelescopeEnabled;
        private static FieldInfo _fBldgResCost, _fEntityCachedColor, _fEntityBaseColor;
        private static PropertyInfo _pBldgCanSupply, _pBldgDisabledManual, _pBldgTotalFoodSup, _pBldgTotalWorkSup, _pBldgTotalEngSup;
        private static PropertyInfo _pBldgTotalWoodSup, _pBldgTotalStoneSup, _pBldgTotalIronSup, _pBldgTotalOilSup, _pBldgTotalGoldSup, _pBldgTotalFoodNeed;
        private static PropertyInfo _pDefMaxInstances, _pDefReturnFactor, _pDefCanEnhance, _pDefResGenTime, _pDefConvertResToGold;
        private static PropertyInfo _pDefColonists, _pDefResStorage, _pDefGoldGen, _pDefGoldPerCol, _pDefFoodNeed, _pDefWorkNeed, _pDefWorkSupply, _pDefEngNeed;
        private static PropertyInfo _pDefWoodGen, _pDefStoneGen, _pDefIronGen, _pDefOilGen, _pDefResCollectRadius, _pDefGoldCost, _pDefLevel;
        private static PropertyInfo _pDefMinColonists, _pDefBuildingRequisite, _pDefEntityName;
        private static MethodInfo _mCmdGetCost, _mCmdGetDefaultParams;
        private static PropertyInfo _pCmdDefBuildingRequisite;
        private static FieldInfo _fCmdErrorMsg;
        private static PropertyInfo _pBldgOnlyOnceCmdsExecuted;
        private static PropertyInfo _pMayorBonusEntities, _pMayorBonusTechs, _pMayorExtraGold, _pMayorExtraWood, _pMayorExtraStone, _pMayorExtraIron, _pMayorExtraOil;
        private static PropertyInfo _pMayorNameProp, _pMayorIdProp, _pMayorLevelProp, _pMayorQuoteProp, _pMayorActiveProp, _pMayorCustomProp;

        // 14. Phase 4 M4 Multi-Base Map Generator & Multi-Wave Swarms Handles
        private static PropertyInfo _propMapThemeNumDoomVillages;
        private static PropertyInfo _propMapDrawerLayerZombies;
        private static PropertyInfo _propLevelEventNTimesTriggered;
        private static PropertyInfo _propLevelEventShowCountdown;
        private static PropertyInfo _propLevelEventMaxRepetitions;
        private static PropertyInfo _propLevelEventAttackCC;
        private static PropertyInfo _propLevelEventFactorUnitsPerRep;
        private static PropertyInfo _propLevelEventMaxFactorUnitsPerRep;
        private static PropertyInfo _propLevelEventMessage;
        private static PropertyInfo _propLevelEventGenerators;
        private static PropertyInfo _propLevelEventCurrentUnitGenerators;
        private static Type _typeCUnitGenerator;
        private static Type _typeGenerationRequest;
        private static PropertyInfo _propUnitGenConfig;
        private static PropertyInfo _propUnitGenConfigName;
        private static PropertyInfo _propGenReqIDEvent;
        private static PropertyInfo _propGenReqCount;
        private static PropertyInfo _propGenReqGenSpeed;
        private static PropertyInfo _propGenReqAttackCC;
        private static MethodInfo _methodGenReqGetTemplate;
        private static MethodInfo _methodCatalogDoomNestLarge, _methodCatalogDoomNestMedium, _methodCatalogDoomNestSmall;
        private static Type _typeCInfectionNest;
        private static PropertyInfo _propInfectionNestConfig;
        private static PropertyInfo _propInfectionNestFactor;
        private static Type _typeCMiniMapInfo;
        private static PropertyInfo _propZombieCellsAwayRoaming;
        public static Type TypeZombieGiant { get; private set; }
        public static Type TypeZombieMutant { get; private set; }
        public static Type TypeZombieStrongA { get; private set; }
        public static Type TypeZombieVenom { get; private set; }
        public static Type TypeZombieHarpy { get; private set; }
        public static Type TypeZombieDressedA { get; private set; }
        public static Type TypeZombieWeakA { get; private set; }
        public static Type TypeZombieMediumA { get; private set; }
        public static Type TypeZombieWorkerA { get; private set; }

        /// <summary>
        /// Optional predicate set by FactionOwnershipTracker in Multi-Faction mode
        /// to restrict GetSelectedPlayerEntityIds() to entities owned by LocalPlayerId.
        /// </summary>
        public static Func<DXEntity, bool> LocalEntityControlFilter;

        public static bool IsInitialized { get; private set; }

        public static bool InitializeAndValidate()
        {
            if (IsInitialized) return true;

            try
            {
                GameAssembly = typeof(ZX.Program).Assembly;
                EngineAssembly = typeof(DXGame).Assembly;

                int missingCount = 0;
                int verifiedCount = 0;
                void Check(object member, string description)
                {
                    if (member == null)
                    {
                        missingCount++;
                        ModLogger.Error($"[GameBridge] MISSING BINDING: {description}");
                    }
                    else
                    {
                        verifiedCount++;
                        ModLogger.Verbose($"[GameBridge] Verified: {description}");
                    }
                }

                // 1. Resolve Obfuscated Core Types
                TypeZXGame = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXGame);
                TypeZXSystem = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSystem);
                TypeZXSystemGameLevel = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSystemGameLevel);
                TypeZXSystemMainMenu = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSystemMainMenu);
                TypeZXMessageBoxHelper = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXMessageBoxHelper);
                TypeZXWindowMessage = GameAssembly.GetType("ZX.Editor.ZXWindowMessage");
                TypeZXSteamManager = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSteamManager);
                TypeDXEditBoxTypeOfInput = EngineAssembly.GetType("DXVision.DXEditBoxObject+TypeOfInput");

                Check(TypeZXGame, $"Type ZXGame ({ObfuscatedSymbols.Type_ZXGame})");
                Check(TypeZXSystemGameLevel, $"Type ZXSystemGameLevel ({ObfuscatedSymbols.Type_ZXSystemGameLevel})");
                Check(TypeZXSystemMainMenu, $"Type ZXSystemMainMenu ({ObfuscatedSymbols.Type_ZXSystemMainMenu})");
                Check(TypeZXMessageBoxHelper, $"Type ZXMessageBoxHelper ({ObfuscatedSymbols.Type_ZXMessageBoxHelper})");
                Check(TypeZXWindowMessage, "Type ZXWindowMessage (ZX.Editor.ZXWindowMessage)");
                Check(TypeDXEditBoxTypeOfInput, "Type DXEditBoxObject.TypeOfInput");

                // 2. Singleton Getters
                _mZXGameCurrent = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);
                _mLevelSysCurrent = AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);
                _mMainMenuCurrent = AccessTools.Method(TypeZXSystemMainMenu, ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);
                _mGameStateCurrent = AccessTools.Method(typeof(ZXGameState), ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);
                _mLevelStateCurrent = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);

                Check(_mZXGameCurrent, "ZXGame.Current getter");
                Check(_mLevelSysCurrent, "ZXSystemGameLevel.Current getter");
                Check(_mMainMenuCurrent, "ZXSystemMainMenu.Current getter");
                Check(_mGameStateCurrent, "ZXGameState.Current getter");
                Check(_mLevelStateCurrent, "ZXLevelState.Current getter");

                // 3. Save / Load & Localization on ZXGame
                _mLocalize = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_Localize, new[] { typeof(string) });
                _mGetSavesDir = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_GetSavesDirectory, Type.EmptyTypes);
                _methodWriteSaveFile = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_WriteSaveFile, new[] { typeof(string) });
                _methodSaveGameFlow = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_SaveGameFlow, new[] { typeof(string), typeof(Action), typeof(bool), typeof(bool) });
                _methodLoadGameAndEnter = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_LoadGameAndEnter, new[] { typeof(string) });

                Check(_mLocalize, "ZXGame.Localize");
                Check(_mGetSavesDir, "ZXGame.GetSavesDirectory");
                Check(_methodWriteSaveFile, "ZXGame.WriteSaveFile (.zxsav)");
                Check(_methodSaveGameFlow, "ZXGame.SaveGameFlow");
                Check(_methodLoadGameAndEnter, "ZXGame.LoadGameAndEnter (.zxsav)");

                // 4. Level System Methods
                _methodShowTopBanner = AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ShowTopBanner,
                    new[] { typeof(string), typeof(Color), typeof(int) });
                _methodShowGroundIndicator = TypeZXSystemGameLevel?.GetMethod(
                    ObfuscatedSymbols.Method_LevelSys_ShowGroundIndicator,
                    BindingFlags.Public | BindingFlags.Instance);
                _methodLevelSysOpenGameMenu = TypeZXSystemGameLevel?.GetMethod(
                    ObfuscatedSymbols.Method_LevelSys_OpenGameMenu,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _methodAssignGroupMove = AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_AssignGroupMove,
                    new[] { typeof(List<DXEntity>), typeof(ZXCommand), typeof(PointF) });

                Check(_methodShowTopBanner, "ZXSystemGameLevel.ShowTopBanner");
                Check(_methodLevelSysOpenGameMenu, "ZXSystemGameLevel.OpenGameMenu");
                Check(_methodAssignGroupMove, "ZXSystemGameLevel.AssignGroupMove");

                // 5. Command Execution & Lookup
                _mCmdById = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_GetById, new[] { typeof(int) });
                _mCmdByName = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_GetByName, new[] { typeof(string) });
                _mCmdExec = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_Execute,
                    new[] { typeof(ZXEntity), typeof(ZXCommandTarget) });
                _methodCanExecuteCmd = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_CanExecute,
                    new[] { typeof(ZXEntity) });
                _methodIsVisibleCmd = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_IsVisible,
                    new[] { typeof(ZXEntity) });
                _methodGetResourcesCost = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_GetResourcesCost,
                    new[] { typeof(ZXEntity) });
                _typeCreateEntityCommand = GameAssembly.GetType("ZX.Commands.CreateEntityCommand");
                TypeStructure = GameAssembly.GetType("ZX.Entities.Structure");
                TypeColonist = GameAssembly.GetType(ObfuscatedSymbols.Type_Colonist);
                TypeBuilding = GameAssembly.GetType(ObfuscatedSymbols.Type_Building);
                _typeEntityBonusCreation = GameAssembly.GetType("ZX.Commands.EntityBonusCreation");
                _typeExtractFromSlot = GameAssembly.GetType("ZX.Commands.ExtractFromUnitHolderSlot");
                _typeContractMercenary = GameAssembly.GetType(ObfuscatedSymbols.Type_ContractMercenary);
                TypeTheInn = GameAssembly.GetType(ObfuscatedSymbols.Type_TheInn);
                _propBonusCmdNSlot = _typeEntityBonusCreation?.GetProperty("NSlot", BindingFlags.Public | BindingFlags.Instance);
                _propBonusCmdTemplate = _typeEntityBonusCreation?.GetProperty("EntityTemplate", BindingFlags.Public | BindingFlags.Instance);
                _propBonusCmdEntity = _typeEntityBonusCreation?.GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
                _propExtractCmdNSlot = _typeExtractFromSlot?.GetProperty("NSlot", BindingFlags.Public | BindingFlags.Instance);
                _propMercCmdNSlot = _typeContractMercenary?.GetProperty("NSlot", BindingFlags.Public | BindingFlags.Instance);
                _propMercCmdTemplate = _typeContractMercenary?.GetProperty("EntityTemplate", BindingFlags.Public | BindingFlags.Instance);
                _propMercCmdEntity = _typeContractMercenary?.GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
                _mTheInnGetMercenaries = TypeTheInn != null
                    ? AccessTools.Method(TypeTheInn, ObfuscatedSymbols.Method_TheInn_GetMercenaries, Type.EmptyTypes)
                    : null;
                _propCmdableAvailableCmds = typeof(CCommandable).GetProperty("AvailableCommands", BindingFlags.Public | BindingFlags.Instance);
                _fieldZXGameAllCommands = TypeZXGame?.GetField(ObfuscatedSymbols.Field_ZXGame_AllCommandsList, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _methodLevelStateConsumeBonus = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_ZXLevelState_ConsumeBonus, new[] { typeof(DXEntityTemplate) });
                _methodLevelSysClearPendingMode = AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ClearPendingBuildMode, Type.EmptyTypes);
                _methodPlaceBuilding = AccessTools.Method(typeof(Build), ObfuscatedSymbols.Method_Build_PlaceBuilding,
                    new[] { typeof(ZXEntity), typeof(ZXCommandTarget), typeof(Type), typeof(bool), typeof(bool), typeof(DXEntityTemplate) });
                _methodGetTemplateFromEntityType = AccessTools.Method(TypeZXGame, "GetTemplateFromEntityType", new[] { typeof(Type) });
                _methodEntityCanPlaceAtCell = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_CanPlaceAtCell,
                    new[] { typeof(int), typeof(int) });
                _methodEntityUpdateCellPosition = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_UpdateCellPosition,
                    Type.EmptyTypes);

                Check(_mCmdById, "ZXCommand.GetById");
                Check(_mCmdByName, "ZXCommand.GetByName");
                Check(_mCmdExec, "ZXCommand.Execute");
                Check(_methodCanExecuteCmd, "ZXCommand.CanExecute");
                Check(_methodIsVisibleCmd, "ZXCommand.IsVisible");
                Check(_methodGetResourcesCost, "ZXCommand.GetResourcesCost");
                Check(_typeCreateEntityCommand, "Type ZX.Commands.CreateEntityCommand");
                Check(TypeStructure, "Type ZX.Entities.Structure");
                Check(TypeColonist, "Type ZX.Entities.Colonist");
                Check(TypeBuilding, "Type ZX.Entities.Building");
                Check(_typeEntityBonusCreation, "Type ZX.Commands.EntityBonusCreation");
                Check(_typeExtractFromSlot, "Type ZX.Commands.ExtractFromUnitHolderSlot");
                Check(_typeContractMercenary, "Type ZX.Commands.ContractMercenary");
                Check(TypeTheInn, "Type ZX.Entities.TheInn");
                Check(_mTheInnGetMercenaries, "TheInn.GetMercenaries");
                Check(_methodLevelStateConsumeBonus, "ZXLevelState.ConsumeBonus");
                Check(_methodPlaceBuilding, "Build.PlaceBuilding");
                Check(_methodGetTemplateFromEntityType, "ZXGame.GetTemplateFromEntityType");
                Check(_methodEntityCanPlaceAtCell, "ZXEntity.CanPlaceAtCell");
                Check(_methodEntityUpdateCellPosition, "ZXEntity.UpdateCellPosition");

                // 6. Entity & Component Helpers
                _mGetEntityCmd = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetCommandable, Type.EmptyTypes);
                _mGetEntitySel = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetSelectable, Type.EmptyTypes);
                _mGetEntityLife = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetLife, Type.EmptyTypes);
                _mGetEntityBehaviour = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetBehaviour, Type.EmptyTypes);
                _mGetEntityMovable = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetMovable, Type.EmptyTypes);
                _typeZombie = GameAssembly.GetType(ObfuscatedSymbols.Type_Zombie);

                Check(_mGetEntityCmd, "ZXEntity.GetCommandable");
                Check(_mGetEntitySel, "ZXEntity.GetSelectable");
                Check(_mGetEntityLife, "ZXEntity.GetLife");
                Check(_mGetEntityBehaviour, "ZXEntity.GetBehaviour");
                Check(_mGetEntityMovable, "ZXEntity.GetMovable");
                Check(_typeZombie, "Type ZX.Entities.Zombie");

                _mCurCmd = AccessTools.Method(typeof(CCommandable), ObfuscatedSymbols.Method_CCommandable_GetCurrentCmd, Type.EmptyTypes);
                _mCancelCur = AccessTools.Method(typeof(CCommandable), ObfuscatedSymbols.Method_CCommandable_CancelCurrent, Type.EmptyTypes);
                _mCancelSlot = AccessTools.Method(typeof(CCommandable), ObfuscatedSymbols.Method_CCommandable_CancelQueueAt, new[] { typeof(int) });

                Check(_mCurCmd, "CCommandable.GetCurrentCommand");
                Check(_mCancelCur, "CCommandable.CancelCurrentCommand");
                Check(_mCancelSlot, "CCommandable.CancelQueueSlot");

                // 7. Selection Helpers
                _fieldSelectedList = AccessTools.Field(typeof(CSelectable), ObfuscatedSymbols.Field_CSelectable_SelectedList);
                _mPrimarySel = AccessTools.Method(typeof(CSelectable), ObfuscatedSymbols.Method_CSelectable_GetPrimary, Type.EmptyTypes);
                _mUnselectAll = AccessTools.Method(typeof(CSelectable), ObfuscatedSymbols.Method_CSelectable_UnselectAll, Type.EmptyTypes);

                Check(_fieldSelectedList, "CSelectable.SelectedList field");
                Check(_mPrimarySel, "CSelectable.GetPrimarySelected");
                Check(_mUnselectAll, "CSelectable.UnselectAll");

                // 8. MessageBox & UI Helpers
                _methodShowAlert = AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowAlert,
                    new[] { typeof(string), typeof(string), typeof(Action) });
                _methodShowYesNo = AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowYesNo,
                    new[] { typeof(string), typeof(string), typeof(Action), typeof(Action) });
                _methodShowListSelector = TypeZXWindowMessage != null
                    ? AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowListSelector,
                        new[] { TypeZXWindowMessage, typeof(Action<int>), typeof(Action), typeof(string[]) })
                    : null;
                _methodShowInputBox = TypeDXEditBoxTypeOfInput != null
                    ? AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowInputBox,
                        new[]
                        {
                            typeof(string), typeof(string), typeof(string), TypeDXEditBoxTypeOfInput,
                            typeof(int), typeof(Action<string>), typeof(Action), typeof(Func<string, bool>)
                        })
                    : null;
                _methodGetPlainTextRenderer = AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_GetPlainTextRenderer, Type.EmptyTypes);
                _propWindowMsgTitle = TypeZXWindowMessage != null ? AccessTools.Property(TypeZXWindowMessage, "Title") : null;
                _propWindowMsgText = TypeZXWindowMessage != null ? AccessTools.Property(TypeZXWindowMessage, "Text") : null;

                Check(_methodShowAlert, "ZXMessageBoxHelper.ShowAlert");
                Check(_methodShowYesNo, "ZXMessageBoxHelper.ShowYesNo");
                Check(_methodShowListSelector, "ZXMessageBoxHelper.ShowListSelector");
                Check(_methodShowInputBox, "ZXMessageBoxHelper.ShowInputBox");
                Check(_methodGetPlainTextRenderer, "ZXMessageBoxHelper.GetPlainTextRenderer");
                Check(_propWindowMsgTitle, "ZXWindowMessage.Title");
                Check(_propWindowMsgText, "ZXWindowMessage.Text");

                // 9. Mayor & Modal Window Helpers
                EnsureMayorMeta();
                Check(_typeZXMayor, "Type ZX.ZXMayor");
                Check(_mayorApplyMethod, "ZXMayor.Apply");
                Check(_levelStateIsChoosingMayorMethod, "ZXLevelState.IsChoosingMayor");
                Check(_levelStateSetChoosingMayorMethod, "ZXLevelState.SetChoosingMayor");
                Check(_typeZXWindow, "Type ZX.GUI.ZXWindow");
                Check(_windowCloseMethod, "ZXWindow.Close");

                // 10. Lobby Window & MainMenu Reuse Helpers
                EnsureLobbyUiMeta();
                Check(_typeZXButtonSteam, "Type ZX.GUI.ZXButtonSteam");
                Check(_windowShowMethod, "ZXWindow.ShowWindow");
                Check(_windowAddBottomBtnMethod, "ZXWindow.AddBottomButton");
                Check(_methodReturnToMainMenu, "ZXGame.ReturnToMainMenu");
                Check(_methodMainMenuLoadSave, "ZXSystemMainMenu.LoadSave");
                Check(_methodMainMenuNewSurvival, "ZXSystemMainMenu.NewSurvival");
                Check(_methodMainMenuCustomMap, "ZXSystemMainMenu.CustomMap");
                Check(_fieldMainMenuContinueBtn, "ZXSystemMainMenu.ContinueButton");
                Check(_typeZXFontCatalogFonts, "Type ZXFontCatalog.Fonts");

                // 11. Pending Build Cost & Immediate Unit Voice Helpers
                TypeZXSystemAudio = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSystemAudio);
                _typeUnit = GameAssembly.GetType("ZX.Entities.Unit");
                _methodEntityGetDefaultParams = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetDefaultParams, Type.EmptyTypes);
                _fieldAudioCurrent = TypeZXSystemAudio != null ? AccessTools.Field(TypeZXSystemAudio, ObfuscatedSymbols.Field_Audio_Current) : null;
                _methodAudioPickWeightedVoice = TypeZXSystemAudio != null
                    ? AccessTools.Method(TypeZXSystemAudio, ObfuscatedSymbols.Method_Audio_PickWeightedVoice, new[] { typeof(List<string>) })
                    : null;
                _methodAudioPlayUniqueList = TypeZXSystemAudio != null
                    ? AccessTools.Method(TypeZXSystemAudio, ObfuscatedSymbols.Method_Audio_PlayUnique,
                        new[] { typeof(ZXSoundCategory), typeof(ZXUniqueSoundType), typeof(List<string>) })
                    : null;
                _methodAudioPlay2DSoundList = TypeZXSystemAudio != null
                    ? AccessTools.Method(TypeZXSystemAudio, ObfuscatedSymbols.Method_Audio_Play2DSound,
                        new[] { typeof(ZXSoundCategory), typeof(List<string>) })
                    : null;

                _methodLevelStateAvailFood = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_ZXLevelState_AvailFood, Type.EmptyTypes);
                _methodLevelStateAvailWorkers = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_ZXLevelState_AvailWorkers, Type.EmptyTypes);
                _methodLevelStateAvailEnergy = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_ZXLevelState_AvailEnergy, Type.EmptyTypes);

                Check(TypeZXSystemAudio, $"Type ZXSystemAudio ({ObfuscatedSymbols.Type_ZXSystemAudio})");
                Check(_typeUnit, "Type ZX.Entities.Unit");
                Check(_methodEntityGetDefaultParams, "ZXEntity.GetDefaultParams");
                Check(_fieldAudioCurrent, "ZXSystemAudio.Current");
                Check(_methodAudioPickWeightedVoice, "ZXSystemAudio.PickWeightedVoice");
                Check(_methodAudioPlayUniqueList, "ZXSystemAudio.PlayUnique(List)");
                Check(_methodAudioPlay2DSoundList, "ZXSystemAudio.Play2DSound(List)");
                Check(_methodLevelStateAvailFood, "ZXLevelState.AvailFood");
                Check(_methodLevelStateAvailWorkers, "ZXLevelState.AvailWorkers");
                Check(_methodLevelStateAvailEnergy, "ZXLevelState.AvailEnergy");

                // 12. Random Survival Map Generator & Balance Scaling Handles
                TypeZXMapGenerator = GameAssembly.GetType(ObfuscatedSymbols.Type_MapGenerator);
                TypeCommandCenter = GameAssembly.GetType(ObfuscatedSymbols.Type_CommandCenter);
                TypeRanger = GameAssembly.GetType(ObfuscatedSymbols.Type_Ranger);
                TypeSoldierRegular = GameAssembly.GetType(ObfuscatedSymbols.Type_SoldierRegular);
                _typeZXRandomLevelParams = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXRandomLevelParams);
                _typeZXMapTheme = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXMapTheme);
                _typeZXLevelExtension = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXLevelExtension);
                _typeZXMapDrawer = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXMapDrawer);
                _typeZXLevelEvent = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXLevelEvent);

                _methodMapGeneratorGenerate = (TypeZXMapGenerator != null && _typeZXRandomLevelParams != null)
                    ? AccessTools.Method(TypeZXMapGenerator, ObfuscatedSymbols.Method_MapGenerator_Generate, new[] { _typeZXRandomLevelParams })
                    : null;
                _methodZXMapThemeGetThemes = _typeZXMapTheme != null
                    ? AccessTools.Method(_typeZXMapTheme, ObfuscatedSymbols.Method_ZXMapTheme_GetThemes, Type.EmptyTypes)
                    : null;
                _methodLevelEventScalePop = _typeZXLevelEvent != null
                    ? AccessTools.Method(_typeZXLevelEvent, ObfuscatedSymbols.Method_ZXLevelEvent_ScalePopulation, new[] { typeof(float), typeof(int) })
                    : null;
                _methodLevelSysCapturePanorama = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_CapturePanorama, new[] { typeof(string), typeof(int) })
                    : null;
                _methodLevelSysEnterGameView = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_EnterGameView, Type.EmptyTypes)
                    : null;
                _mInitLevel = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_InitLevel, new[] { typeof(DXLevel) })
                    : null;
                _propLevelStateEndStatus = AccessTools.Property(typeof(ZXLevelState), "EndStatus");
                _fieldLevelSysGameEnded = TypeZXSystemGameLevel != null
                    ? AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_GameEndedFlag)
                    : null;
                _fieldLevelSysScreenAnim = TypeZXSystemGameLevel != null
                    ? AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_ScreenAnim)
                    : null;
                _methodLevelSysTriggerGameEnd = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_TriggerGameEnd, new[] { typeof(bool) })
                    : null;
                _methodLevelSysShowSurvivalEnd = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ShowSurvivalEnd, new[] { typeof(bool), typeof(bool) })
                    : null;
                _methodLevelSysShowCampaignEnd = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ShowCampaignEnd, new[] { typeof(bool), typeof(bool) })
                    : null;
                _methodGameShowMayorSelection = TypeZXGame != null
                    ? AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_ShowMayorSelection, Type.EmptyTypes)
                    : null;
                _typeZXGameState = typeof(ZXGameState);
                _methodLevelSysRefreshSelectionGroupsHud = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_RefreshSelectionGroupsHud, Type.EmptyTypes)
                    : null;
                _methodLevelStateGetSelectionGroups = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_ZXLevelState_GetSelectionGroups, Type.EmptyTypes);
                _typeKeyboardBindingsManager = GameAssembly.GetType(ObfuscatedSymbols.Type_KeyboardBindingsManager);
                _methodKeyboardIsKeyForCommand = _typeKeyboardBindingsManager != null
                    ? AccessTools.Method(_typeKeyboardBindingsManager, ObfuscatedSymbols.Method_Keyboard_IsKeyForCommand, new[] { typeof(DXKeys), typeof(ZXKeyboardCommand) })
                    : null;
                _fieldLevelSysBuildAreaCursor = TypeZXSystemGameLevel != null
                    ? AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_BuildAreaCursor)
                    : null;
                _typeOCellAreaCursor = GameAssembly.GetType(ObfuscatedSymbols.Type_OCellAreaCursor);
                if (_typeOCellAreaCursor != null)
                {
                    _fieldCursorLastEnergyCell = AccessTools.Field(_typeOCellAreaCursor, ObfuscatedSymbols.Field_OCellAreaCursor_LastEnergyCell);
                    _methodCursorClearEnergyPreview = AccessTools.Method(_typeOCellAreaCursor, ObfuscatedSymbols.Method_OCellAreaCursor_ClearEnergyPreview, Type.EmptyTypes);
                    _methodCursorUpdateVisual = AccessTools.Method(_typeOCellAreaCursor, ObfuscatedSymbols.Method_OCellAreaCursor_UpdateVisual, Type.EmptyTypes);
                }
                _typeZXOrder = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXOrder);
                _ctorZXOrder = (_typeZXOrder != null && TypeZXCommandTarget != null)
                    ? _typeZXOrder.GetConstructor(new[] { typeof(ZXCommand), TypeZXCommandTarget })
                    : null;
                _typeCBuildable = GameAssembly.GetType(ObfuscatedSymbols.Type_CBuildable);
                if (_typeCBuildable != null)
                {
                    _propCBuildableFactor = _typeCBuildable.GetProperty("BuildingFactor", BindingFlags.Public | BindingFlags.Instance);
                    _propCBuildableMode = _typeCBuildable.GetProperty("BuildMode", BindingFlags.Public | BindingFlags.Instance);
                }
                _propStructureIsBeingBuilt = TypeStructure?.GetProperty("IsBeingBuilt", BindingFlags.Public | BindingFlags.Instance);
                _methodBuildingRecalcResources = TypeBuilding != null
                    ? AccessTools.Method(TypeBuilding, ObfuscatedSymbols.Method_Building_RecalcResources, Type.EmptyTypes)
                    : null;
                _methodStructureUpdateEnabled = TypeStructure != null
                    ? AccessTools.Method(TypeStructure, ObfuscatedSymbols.Method_Structure_UpdateEnabled, Type.EmptyTypes)
                    : null;
                _methodLevelStateRecordEntityCreated = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_LevelState_RecordEntityCreated, new[] { typeof(DXEntity) });
                _methodLevelSysRefreshCmdPanel = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_RefreshCmdPanel, Type.EmptyTypes)
                    : null;
                _propEntityTeam = TypeZXEntity?.GetProperty("Team", BindingFlags.Public | BindingFlags.Instance);
                _propEntityRelation = TypeZXEntity?.GetProperty("Relation", BindingFlags.Public | BindingFlags.Instance);
                if (TypeCMovable != null)
                {
                    _propMovableTargetPosProp = TypeCMovable.GetProperty("TargetPosition", BindingFlags.Public | BindingFlags.Instance);
                    _propMovablePath = TypeCMovable.GetProperty("Path", BindingFlags.Public | BindingFlags.Instance);
                    _fieldMovablePendingPath = TypeCMovable.GetField(ObfuscatedSymbols.Field_CMovable_PendingPathRequest, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (_typeZXRandomLevelParams != null)
                {
                    _propRandomParamsNCells = AccessTools.Property(_typeZXRandomLevelParams, "NCells");
                    _propRandomParamsFactorDuration = AccessTools.Property(_typeZXRandomLevelParams, "FactorGameDuration");
                    _propRandomParamsFactorZombiePop = AccessTools.Property(_typeZXRandomLevelParams, "FactorZombiePopulation");
                    _propRandomParamsThemeType = AccessTools.Property(_typeZXRandomLevelParams, "ThemeType");
                }
                if (_typeZXMapTheme != null)
                {
                    _propMapThemeNumDoomVillages = AccessTools.Property(_typeZXMapTheme, "NumDoomVillages");
                    _propMapThemeDoomVillagesSize = AccessTools.Property(_typeZXMapTheme, "DoomVillagesSize");
                    _propMapThemeNumTreasures = AccessTools.Property(_typeZXMapTheme, "NumTreasures");
                    _propMapThemeNumSuppyFood = AccessTools.Property(_typeZXMapTheme, "NumSuppyFood");
                    _propMapThemeNumSuppyEnergy = AccessTools.Property(_typeZXMapTheme, "NumSuppyEnergy");
                    _propMapThemeNumInfectedGiants = AccessTools.Property(_typeZXMapTheme, "NumInfectedGiants");
                    _propMapThemeNumInfectedMutants = AccessTools.Property(_typeZXMapTheme, "NumInfectedMutants");
                    _propMapThemeInfectedFactor = AccessTools.Property(_typeZXMapTheme, "InfectedFactor");
                    _propMapThemeInfectedDispersion = AccessTools.Property(_typeZXMapTheme, "InfectedDispersion");
                    _propMapThemeMinDistWeak = AccessTools.Property(_typeZXMapTheme, "MinDistanceForWeakInfected");
                    _propMapThemeMinDistMed = AccessTools.Property(_typeZXMapTheme, "MinDistanceForMediumInfected");
                    _propMapThemeMinDistStrong = AccessTools.Property(_typeZXMapTheme, "MinDistanceForStrongInfected");
                    _propMapThemeMinDistPowerful = AccessTools.Property(_typeZXMapTheme, "MinDistanceForPowerfulInfected");
                }
                if (_typeZXLevelExtension != null)
                {
                    _propLevelExtMapDrawer = AccessTools.Property(_typeZXLevelExtension, "MapDrawer");
                    _propLevelExtLevelEvents = AccessTools.Property(_typeZXLevelExtension, "LevelEvents");
                    _propLevelExtHideEnemies = AccessTools.Property(_typeZXLevelExtension, "HideEnemiesIfNotWatched");
                    _methodLevelExtGetCurrent = AccessTools.Method(_typeZXLevelExtension, "#=zuartwoo=", Type.EmptyTypes);
                }
                if (_typeZXMapDrawer != null)
                {
                    _propMapDrawerExtraEntities = AccessTools.Property(_typeZXMapDrawer, "ExtraEntities");
                    _propMapDrawerLayerTerrain = AccessTools.Property(_typeZXMapDrawer, "LayerTerrain");
                    _propMapDrawerLayerObjects = AccessTools.Property(_typeZXMapDrawer, "LayerObjects");
                    _propMapDrawerLayerZombies = AccessTools.Property(_typeZXMapDrawer, "LayerZombies");
                    _propMapDrawerNCells = AccessTools.Property(_typeZXMapDrawer, "NCells");
                }
                if (_typeZXLevelEvent != null)
                {
                    _propLevelEventStartTimeH = AccessTools.Property(_typeZXLevelEvent, "StartTimeH");
                    _propLevelEventGameTimeH = AccessTools.Property(_typeZXLevelEvent, "GameTimeH");
                    _propLevelEventFinalSwarm = AccessTools.Property(_typeZXLevelEvent, "FinalSwarm");
                    _propLevelEventAllInfectedToCC = AccessTools.Property(_typeZXLevelEvent, "AllInfectedToCommandCenter");
                    _propLevelEventNTimesTriggered = AccessTools.Property(_typeZXLevelEvent, "NTimesTriggered");
                    _propLevelEventShowCountdown = AccessTools.Property(_typeZXLevelEvent, "ShowCountdown");
                    _propLevelEventMaxRepetitions = AccessTools.Property(_typeZXLevelEvent, "MaxRepetitions");
                    _propLevelEventAttackCC = AccessTools.Property(_typeZXLevelEvent, "AttackCommandCenter");
                    _propLevelEventFactorUnitsPerRep = AccessTools.Property(_typeZXLevelEvent, "FactorUnitsNumberPerRepetition");
                    _propLevelEventMaxFactorUnitsPerRep = AccessTools.Property(_typeZXLevelEvent, "MaxFactorUnitsNumberPerRepetition");
                    _propLevelEventMessage = AccessTools.Property(_typeZXLevelEvent, "Message");
                    _propLevelEventGenerators = AccessTools.Property(_typeZXLevelEvent, "Generators");
                    _propLevelEventCurrentUnitGenerators = AccessTools.Property(_typeZXLevelEvent, "CurrentUnitGenerators");
                }

                _typeCUnitGenerator = GameAssembly.GetType(ObfuscatedSymbols.Type_CUnitGenerator);
                if (_typeCUnitGenerator != null)
                {
                    _propUnitGenConfig = AccessTools.Property(_typeCUnitGenerator, "Configuration");
                    _propUnitGenConfigName = _propUnitGenConfig?.PropertyType?.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    _typeGenerationRequest = _typeCUnitGenerator.GetNestedType("GenerationRequest", BindingFlags.Public | BindingFlags.NonPublic);
                    if (_typeGenerationRequest != null)
                    {
                        _propGenReqIDEvent = AccessTools.Property(_typeGenerationRequest, "IDEvent");
                        _propGenReqCount = AccessTools.Property(_typeGenerationRequest, "Count");
                        _propGenReqGenSpeed = AccessTools.Property(_typeGenerationRequest, "GenerationSpeed");
                        _propGenReqAttackCC = AccessTools.Property(_typeGenerationRequest, "AttackCommandCenter");
                        _methodGenReqGetTemplate = AccessTools.Method(_typeGenerationRequest, ObfuscatedSymbols.Method_GenerationRequest_GetTemplate, Type.EmptyTypes);
                    }
                }
                _typeCInfectionNest = GameAssembly.GetType(ObfuscatedSymbols.Type_CInfectionNest);
                if (_typeCInfectionNest != null)
                {
                    _propInfectionNestConfig = AccessTools.Property(_typeCInfectionNest, "Configuration");
                    _propInfectionNestFactor = _propInfectionNestConfig?.PropertyType?.GetProperty("InfectedFactor", BindingFlags.Public | BindingFlags.Instance);
                }
                _typeCMiniMapInfo = GameAssembly.GetType(ObfuscatedSymbols.Type_CMiniMapInfo);
                if (_typeZombie != null)
                {
                    _propZombieCellsAwayRoaming = AccessTools.Property(_typeZombie, "CellsAwayCommandCenterWhenRoaming");
                }
                TypeZombieGiant = GameAssembly.GetType(ObfuscatedSymbols.Type_ZombieGiant);
                TypeZombieMutant = GameAssembly.GetType(ObfuscatedSymbols.Type_ZombieMutant);
                TypeZombieStrongA = GameAssembly.GetType(ObfuscatedSymbols.Type_ZombieStrongA);
                TypeZombieVenom = GameAssembly.GetType(ObfuscatedSymbols.Type_ZombieVenom);
                TypeZombieHarpy = GameAssembly.GetType(ObfuscatedSymbols.Type_ZombieHarpy);
                TypeZombieDressedA = GameAssembly.GetType(ObfuscatedSymbols.Type_ZombieDressedA);
                TypeZombieWeakA = GameAssembly.GetType(ObfuscatedSymbols.Type_ZombieWeakA);
                TypeZombieMediumA = GameAssembly.GetType(ObfuscatedSymbols.Type_ZombieMediumA);
                TypeZombieWorkerA = GameAssembly.GetType(ObfuscatedSymbols.Type_ZombieWorkerA);
                var catalogBuildingsType = GameAssembly.GetType($"{ObfuscatedSymbols.Type_EntityTemplateCatalog}+{ObfuscatedSymbols.NestedType_Catalog_Buildings}");
                if (catalogBuildingsType != null)
                {
                    _methodCatalogDoomNestLarge = AccessTools.Method(catalogBuildingsType, ObfuscatedSymbols.Method_Catalog_DoomNestLarge, Type.EmptyTypes);
                    _methodCatalogDoomNestMedium = AccessTools.Method(catalogBuildingsType, ObfuscatedSymbols.Method_Catalog_DoomNestMedium, Type.EmptyTypes);
                    _methodCatalogDoomNestSmall = AccessTools.Method(catalogBuildingsType, ObfuscatedSymbols.Method_Catalog_DoomNestSmall, Type.EmptyTypes);
                }
                var lsType = LevelStateType ?? GameAssembly.GetType("ZX.ZXLevelState");
                if (lsType != null)
                {
                    _propLevelStateExtraWorkers = AccessTools.Property(lsType, "CommandCenterExtraWorkers");
                    _propLevelStateExtraFood = AccessTools.Property(lsType, "CommandCenterExtraFood");
                    _methodLevelStateRecalcResources = AccessTools.Method(lsType, ObfuscatedSymbols.Method_ZXLevelState_RecalcResources, Type.EmptyTypes);
                    _fieldLevelStateCachedCC = AccessTools.Field(lsType, ObfuscatedSymbols.Field_ZXLevelState_CachedCC);
                    _methodLevelStateRefreshCCCell = AccessTools.Method(lsType, ObfuscatedSymbols.Method_ZXLevelState_RefreshCCCell, Type.EmptyTypes);
                    _propLevelStateCurrentCCCell = AccessTools.Property(lsType, "CurrentCommandCenterCell");
                    _propLevelStateInfectedDestiny = AccessTools.Property(lsType, "DefaultCellInfectedDestiny");
                    _propLevelStateScriptVars = AccessTools.Property(lsType, "ScriptVariables");
                }
                _propGameStateRandom = _typeZXGameState?.GetProperty("Random", BindingFlags.Public | BindingFlags.Instance);

                _typeZXSystemMapLayers = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSystemMapLayers);
                if (_typeZXSystemMapLayers != null)
                {
                    _fieldMapLayersCurrent = AccessTools.Field(_typeZXSystemMapLayers, ObfuscatedSymbols.Field_MapLayers_Current);
                    _fieldMapLayersEnergyGrid = AccessTools.Field(_typeZXSystemMapLayers, ObfuscatedSymbols.Field_MapLayers_EnergyGrid);
                    _fieldMapLayersEnergyFrontier = AccessTools.Field(_typeZXSystemMapLayers, ObfuscatedSymbols.Field_MapLayers_EnergyFrontier);
                    _fieldMapLayersEnergySources = AccessTools.Field(_typeZXSystemMapLayers, ObfuscatedSymbols.Field_MapLayers_EnergySources);
                    _fieldMapLayersEnergyLastIdx = AccessTools.Field(_typeZXSystemMapLayers, ObfuscatedSymbols.Field_MapLayers_EnergyLastIdx);
                    _fieldMapLayersEnergyInitDone = AccessTools.Field(_typeZXSystemMapLayers, ObfuscatedSymbols.Field_MapLayers_EnergyInitDone);
                    _fieldMapLayersEnergyLock = AccessTools.Field(_typeZXSystemMapLayers, ObfuscatedSymbols.Field_MapLayers_EnergyLock);
                    _propMapLayersNEnergyChanges = AccessTools.Property(_typeZXSystemMapLayers, "NEnergyMapChanges");
                    _propMapLayersLayerEnergyUpdated = AccessTools.Property(_typeZXSystemMapLayers, "LayerEnergyUpdated");
                    _methodMapLayersRebuildEnergyGrid = AccessTools.Method(_typeZXSystemMapLayers, ObfuscatedSymbols.Method_MapLayers_RebuildEnergyGrid, Type.EmptyTypes);
                    _methodMapLayersHasPathToAnyCC = AccessTools.Method(_typeZXSystemMapLayers, ObfuscatedSymbols.Method_MapLayers_HasPathToAnyCC, new[] { typeof(Point) });
                    _methodMapLayersHasPathBetween = AccessTools.Method(_typeZXSystemMapLayers, ObfuscatedSymbols.Method_MapLayers_HasPathBetween, new[] { typeof(Point), typeof(Point) });
                }
                TypeCEnergyTransferer = GameAssembly.GetType(ObfuscatedSymbols.Type_CEnergyTransferer);
                _methodEnergyTransfererUpdateGrid = TypeCEnergyTransferer != null
                    ? AccessTools.Method(TypeCEnergyTransferer, ObfuscatedSymbols.Method_CEnergyTransferer_UpdateGrid, Type.EmptyTypes)
                    : null;
                _fieldBuildingHasEnergyConn = TypeBuilding != null
                    ? AccessTools.Field(TypeBuilding, ObfuscatedSymbols.Field_Building_HasEnergyConnection)
                    : null;
                _methodBuildingRefreshEnergy = TypeBuilding != null
                    ? AccessTools.Method(TypeBuilding, ObfuscatedSymbols.Method_Building_RefreshEnergy, new[] { typeof(bool) })
                    : null;
                _methodLevelSysIsCellPlayableForEnergy = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_IsCellPlayableForEnergy, new[] { typeof(int), typeof(int) })
                    : null;
                TypeCNeutralJoinable = GameAssembly.GetType(ObfuscatedSymbols.Type_CNeutralJoinable);
                TypeCLife = GameAssembly.GetType(ObfuscatedSymbols.Type_CLife);
                TypeTrain = GameAssembly.GetType(ObfuscatedSymbols.Type_Train);
                TypeUpgrade = GameAssembly.GetType(ObfuscatedSymbols.Type_Upgrade);
                TypeCInvisibleWhenNotWatched = GameAssembly.GetType(ObfuscatedSymbols.Type_CInvisibleWhenNotWatched);
                _methodCInvisibleUpdateOpacity = TypeCInvisibleWhenNotWatched != null
                    ? AccessTools.Method(TypeCInvisibleWhenNotWatched, ObfuscatedSymbols.Method_CInvisibleWhenNotWatched_UpdateOpacity, Type.EmptyTypes)
                    : null;
                _fieldLevelSysCurrentCC = TypeZXSystemGameLevel != null
                    ? AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_CurrentCC)
                    : null;
                _propBuildingInfected = TypeBuilding?.GetProperty("Infected", BindingFlags.Public | BindingFlags.Instance);
                if (TypeZXEntity != null)
                {
                    var defParamsType = _methodEntityGetDefaultParams?.ReturnType;
                    _propDefaultParamsEnergyRadius = defParamsType?.GetProperty("EnergyTransferRadius", BindingFlags.Public | BindingFlags.Instance);
                }
                _typeBData = GameAssembly.GetType("ZX.Behaviours.BData");
                if (_typeBData != null)
                {
                    _methodBDataGetOwner = AccessTools.Method(_typeBData, ObfuscatedSymbols.Method_BData_GetOwner, Type.EmptyTypes);
                    _propBDataTargetPos = _typeBData.GetProperty("TargetPosition", BindingFlags.Public | BindingFlags.Instance);
                    _fieldBDataTargetRef = _typeBData.GetField(ObfuscatedSymbols.Field_BData_TargetRef, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }

                // 14. Phase 4 M2 & M3 bindings
                TypeTechnology = GameAssembly.GetType(ObfuscatedSymbols.Type_Technology);
                TypeUndoBuilding = GameAssembly.GetType(ObfuscatedSymbols.Type_UndoBuilding);
                TypeDestroy = GameAssembly.GetType(ObfuscatedSymbols.Type_Destroy);
                TypeCResourceGenerator = GameAssembly.GetType(ObfuscatedSymbols.Type_CResourceGenerator);
                TypePickableItem = GameAssembly.GetType(ObfuscatedSymbols.Type_PickableItem);
                TypeTheGreatTelescope = GameAssembly.GetType("ZX.Entities.TheGreatTelescope");
                TypeTheAcademy = GameAssembly.GetType("ZX.Entities.TheAcademy");
                TypeTheSpire = GameAssembly.GetType("ZX.Entities.TheSpire");
                TypeWoodWorkshop = GameAssembly.GetType("ZX.Entities.WoodWorkshop");
                TypeStoneWorkshop = GameAssembly.GetType("ZX.Entities.StoneWorkshop");
                TypeFoundry = GameAssembly.GetType("ZX.Entities.Foundry");
                TypeTheCrystalPalace = GameAssembly.GetType("ZX.Entities.TheCrystalPalace");
                TypeTheTransmutator = GameAssembly.GetType("ZX.Entities.TheTransmutator");
                TypeTheVictorious = GameAssembly.GetType("ZX.Entities.TheVictorious");
                TypeTechGreatTelescope = GameAssembly.GetType("ZX.Commands.TechTheGreatTelescope");
                TypeTechSoldierAcademy = GameAssembly.GetType("ZX.Commands.TechTheSoldierAcademy");
                TypeTechElectricSpire = GameAssembly.GetType("ZX.Commands.TechTheElectricSpire");
                TypeZXResourcesCost = GameAssembly.GetType("ZX.ZXResourcesCost");
                TypeZXMayor = GameAssembly.GetType("ZX.ZXMayor");

                if (lsType != null)
                {
                    BindingFlags bfInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    _fLsWood = lsType.GetField("#=z7B6S4Sy2cfMB", bfInst);
                    _fLsStone = lsType.GetField("#=zmeRyIHKzw5Wa", bfInst);
                    _fLsIron = lsType.GetField("#=z4eMPvDrfJ8a8", bfInst);
                    _fLsOil = lsType.GetField("#=zblsCLsuBmakA", bfInst);
                    _fLsGold = lsType.GetField("#=zzOFOrHo=", bfInst);
                    _fLsStorage = lsType.GetField(ObfuscatedSymbols.Field_LevelState_ResourceStorage, bfInst);
                    _fLsFoodSup = lsType.GetField(ObfuscatedSymbols.Field_LevelState_FoodSupply, bfInst);
                    _fLsWorkSup = lsType.GetField(ObfuscatedSymbols.Field_LevelState_WorkersSupply, bfInst);
                    _fLsEngSup = lsType.GetField(ObfuscatedSymbols.Field_LevelState_EnergySupply, bfInst);
                    _fLsFoodNeed = lsType.GetField(ObfuscatedSymbols.Field_LevelState_FoodNeeded, bfInst);
                    _fLsWorkNeed = lsType.GetField(ObfuscatedSymbols.Field_LevelState_WorkersNeeded, bfInst);
                    _fLsEngNeed = lsType.GetField(ObfuscatedSymbols.Field_LevelState_EnergyNeeded, bfInst);
                    _fLsColonists = lsType.GetField(ObfuscatedSymbols.Field_LevelState_Colonists, bfInst);
                    _fLsGoldByCc = lsType.GetField(ObfuscatedSymbols.Field_LevelState_GoldByCC, bfInst);
                    _fLsTotalStock = lsType.GetField(ObfuscatedSymbols.Field_LevelState_TotalStock, bfInst);

                    _pLsExtraEnergy = lsType.GetProperty("CommandCenterExtraEnergy", bfInst);
                    _pLsFoodRes = lsType.GetProperty("FoodReserved", bfInst);
                    _pLsWorkRes = lsType.GetProperty("WorkersReserved", bfInst);
                    _pLsEngRes = lsType.GetProperty("EnergyReserved", bfInst);
                    _pLsWoodProd = lsType.GetProperty("WoodProduction", bfInst);
                    _pLsStoneProd = lsType.GetProperty("StoneProduction", bfInst);
                    _pLsIronProd = lsType.GetProperty("IronProduction", bfInst);
                    _pLsOilProd = lsType.GetProperty("OilProduction", bfInst);
                    _pLsGoldProd = lsType.GetProperty("GoldProduction", bfInst);
                    _pLsWoodProdRes = lsType.GetProperty("WoodProductionReserved", bfInst);
                    _pLsStoneProdRes = lsType.GetProperty("StoneProductionReserved", bfInst);
                    _pLsIronProdRes = lsType.GetProperty("IronProductionReserved", bfInst);
                    _pLsOilProdRes = lsType.GetProperty("OilProductionReserved", bfInst);
                    _pLsNegGoldTurns = lsType.GetProperty("NTurnsWithNegativeGold", bfInst);
                    _pLsNegFoodTurns = lsType.GetProperty("NTurnsWithNegativeFood", bfInst);
                    _pLsNMarkets = lsType.GetProperty("NMarkets", bfInst);
                    _pLsMaxGoldExport = lsType.GetProperty("MaxGoldByExportingExcedents", bfInst);
                    _pLsMaxColonists = lsType.GetProperty("MaxColonists", bfInst);
                    _pLsArmyGold = lsType.GetProperty("ArmyGoldCost", bfInst);
                    _pLsArmyFoodPen = lsType.GetProperty("ArmyFoodPenaltyCost", bfInst);
                    _pLsStructGold = lsType.GetProperty("StructuresGoldCost", bfInst);
                    _pLsGoldPerCol = lsType.GetProperty("TotalGoldPerColonists", bfInst);
                    _pLsBonusTemplates = lsType.GetProperty("BonusEntityTemplates", bfInst);
                    _pLsOnlyOneCmds = lsType.GetProperty("OnlyOneExecutedCommandsFinished", bfInst);
                    _pLsShowFullMap = lsType.GetProperty("ShowFullMap", bfInst);
                    _pLsCurrentCc = lsType.GetProperty("CurrentCommandCenter", bfInst);

                    _mLsRecalculate = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_Recalculate, Type.EmptyTypes);
                    _mLsSetEnhancerBuildings = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_SetEnhancerBuildings);
                    _mLsAddWoodMarket = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_AddWoodWithMarket, new[] { typeof(int) });
                    _mLsAddStoneMarket = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_AddStoneWithMarket, new[] { typeof(int) });
                    _mLsAddIronMarket = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_AddIronWithMarket, new[] { typeof(int) });
                    _mLsAddOilMarket = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_AddOilWithMarket, new[] { typeof(int) });
                    _mLsAddGoldDirect = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_AddGoldDirect, new[] { typeof(int) });
                    if (TypeZXResourcesCost != null)
                    {
                        _mLsAddCost = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_AddCost, new[] { TypeZXResourcesCost });
                        _mLsConsumeCost = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_ConsumeCost, new[] { TypeZXResourcesCost });
                    }
                    if (TypeZXCommand != null)
                    {
                        _mLsAddOnlyOne = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_AddOnlyOneExecuted, new[] { TypeZXCommand, typeof(bool) });
                        _mLsHasOnlyOne = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_HasOnlyOneExecuted, new[] { TypeZXCommand });
                    }
                    _mLsSellWood = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_GetSellFactorWood, Type.EmptyTypes);
                    _mLsSellStone = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_GetSellFactorStone, Type.EmptyTypes);
                    _mLsSellIron = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_GetSellFactorIron, Type.EmptyTypes);
                    _mLsSellOil = AccessTools.Method(lsType, ObfuscatedSymbols.Method_LevelState_GetSellFactorOil, Type.EmptyTypes);
                }

                if (TypeZXResourcesCost != null)
                {
                    _mResCostForType = AccessTools.Method(TypeZXResourcesCost, "#=zg0ptF1Dj8iTQ", new[] { typeof(Type) });
                    _pResGold = TypeZXResourcesCost.GetProperty("Gold", BindingFlags.Public | BindingFlags.Instance);
                    _pResWood = TypeZXResourcesCost.GetProperty("Wood", BindingFlags.Public | BindingFlags.Instance);
                    _pResStone = TypeZXResourcesCost.GetProperty("Stone", BindingFlags.Public | BindingFlags.Instance);
                    _pResIron = TypeZXResourcesCost.GetProperty("Iron", BindingFlags.Public | BindingFlags.Instance);
                    _pResOil = TypeZXResourcesCost.GetProperty("Oil", BindingFlags.Public | BindingFlags.Instance);
                    _pResFood = TypeZXResourcesCost.GetProperty("Food", BindingFlags.Public | BindingFlags.Instance);
                    _pResWorkers = TypeZXResourcesCost.GetProperty("Workers", BindingFlags.Public | BindingFlags.Instance);
                    _pResEnergy = TypeZXResourcesCost.GetProperty("Energy", BindingFlags.Public | BindingFlags.Instance);
                }
                if (TypeCommandCenter != null)
                    _pCcTelescopeEnabled = TypeCommandCenter.GetProperty("TelescopeEnabled", BindingFlags.Public | BindingFlags.Instance);
                if (TypeBuilding != null)
                {
                    BindingFlags bfInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    _fBldgResCost = TypeBuilding.GetField(ObfuscatedSymbols.Field_Building_ResourceCost, bfInst);
                    _pBldgCanSupply = TypeBuilding.GetProperty("CanSupplyResources", bfInst);
                    _pBldgDisabledManual = TypeBuilding.GetProperty("DisabledManually", bfInst);
                    _pBldgTotalFoodSup = TypeBuilding.GetProperty("TotalFoodSupply", bfInst);
                    _pBldgTotalWorkSup = TypeBuilding.GetProperty("TotalWorkersSupply", bfInst);
                    _pBldgTotalEngSup = TypeBuilding.GetProperty("TotalEnergySupply", bfInst);
                    _pBldgTotalWoodSup = TypeBuilding.GetProperty("TotalWoodSupply", bfInst);
                    _pBldgTotalStoneSup = TypeBuilding.GetProperty("TotalStoneSupply", bfInst);
                    _pBldgTotalIronSup = TypeBuilding.GetProperty("TotalIronSupply", bfInst);
                    _pBldgTotalOilSup = TypeBuilding.GetProperty("TotalOilSupply", bfInst);
                    _pBldgTotalGoldSup = TypeBuilding.GetProperty("TotalGoldSupply", bfInst);
                    _pBldgTotalFoodNeed = TypeBuilding.GetProperty("TotalFoodNeeded", bfInst);
                }
                if (TypeZXEntity != null)
                {
                    BindingFlags bfInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    _fEntityCachedColor = TypeZXEntity.GetField(ObfuscatedSymbols.Field_ZXEntity_CachedMinimapColor, bfInst);
                    _fEntityBaseColor = TypeZXEntity.GetField(ObfuscatedSymbols.Field_ZXEntity_BaseMinimapColor, bfInst);
                }
                var dpType = _methodEntityGetDefaultParams?.ReturnType;
                if (dpType != null)
                {
                    _pDefMaxInstances = dpType.GetProperty("MaxInstances", BindingFlags.Public | BindingFlags.Instance);
                    _pDefReturnFactor = dpType.GetProperty("FactorResourcesReturn", BindingFlags.Public | BindingFlags.Instance);
                    _pDefCanEnhance = dpType.GetProperty("CanEnhanceNearBuildings", BindingFlags.Public | BindingFlags.Instance);
                    _pDefResGenTime = dpType.GetProperty("ResourcesGenerationTimeFactor", BindingFlags.Public | BindingFlags.Instance);
                    _pDefConvertResToGold = dpType.GetProperty("ConvertResourcesIntoGold", BindingFlags.Public | BindingFlags.Instance);
                    _pDefColonists = dpType.GetProperty("Colonists", BindingFlags.Public | BindingFlags.Instance);
                    _pDefResStorage = dpType.GetProperty("ResourcesStorage", BindingFlags.Public | BindingFlags.Instance);
                    _pDefGoldGen = dpType.GetProperty("GoldGen", BindingFlags.Public | BindingFlags.Instance);
                    _pDefGoldPerCol = dpType.GetProperty("GoldGenPerColonist", BindingFlags.Public | BindingFlags.Instance);
                    _pDefFoodNeed = dpType.GetProperty("FoodNeeded", BindingFlags.Public | BindingFlags.Instance);
                    _pDefWorkNeed = dpType.GetProperty("WorkersNeeded", BindingFlags.Public | BindingFlags.Instance);
                    _pDefWorkSupply = dpType.GetProperty("WorkersSupply", BindingFlags.Public | BindingFlags.Instance);
                    _pDefEngNeed = dpType.GetProperty("EnergyNeeded", BindingFlags.Public | BindingFlags.Instance);
                    _pDefWoodGen = dpType.GetProperty("WoodGen", BindingFlags.Public | BindingFlags.Instance);
                    _pDefStoneGen = dpType.GetProperty("StoneGen", BindingFlags.Public | BindingFlags.Instance);
                    _pDefIronGen = dpType.GetProperty("IronGen", BindingFlags.Public | BindingFlags.Instance);
                    _pDefOilGen = dpType.GetProperty("OilGen", BindingFlags.Public | BindingFlags.Instance);
                    _pDefResCollectRadius = dpType.GetProperty("ResourceCollectionRadius", BindingFlags.Public | BindingFlags.Instance);
                    _pDefGoldCost = dpType.GetProperty("GoldCost", BindingFlags.Public | BindingFlags.Instance);
                    _pDefLevel = dpType.GetProperty("Level", BindingFlags.Public | BindingFlags.Instance);
                    _pDefMinColonists = dpType.GetProperty("MinColonistsRequisite", BindingFlags.Public | BindingFlags.Instance);
                    _pDefBuildingRequisite = dpType.GetProperty("BuildingRequisite", BindingFlags.Public | BindingFlags.Instance);
                    _pDefEntityName = dpType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                }
                if (TypeZXCommand != null)
                {
                    _mCmdGetDefaultParams = AccessTools.Method(TypeZXCommand, ObfuscatedSymbols.Method_ZXEntity_GetDefaultParams, Type.EmptyTypes);
                    var cmdDpType = _mCmdGetDefaultParams?.ReturnType;
                    if (cmdDpType != null)
                        _pCmdDefBuildingRequisite = cmdDpType.GetProperty("BuildingRequisite", BindingFlags.Public | BindingFlags.Instance);
                    _fCmdErrorMsg = TypeZXCommand.GetField("#=z4gwFgFCco_Z9", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (TypeBuilding != null)
                {
                    _pBldgOnlyOnceCmdsExecuted = TypeBuilding.GetProperty("OnlyOnceCommandsExecuted", BindingFlags.Public | BindingFlags.Instance);
                }
                if (TypeZXCommand != null && TypeZXEntity != null)
                    _mCmdGetCost = AccessTools.Method(TypeZXCommand, ObfuscatedSymbols.Method_ZXCommand_GetCost, new[] { TypeZXEntity });
                if (TypeZXMayor != null)
                {
                    _pMayorBonusEntities = TypeZXMayor.GetProperty("BonusEntities", BindingFlags.Public | BindingFlags.Instance);
                    _pMayorBonusTechs = TypeZXMayor.GetProperty("BonusTechnologies", BindingFlags.Public | BindingFlags.Instance);
                    _pMayorExtraGold = TypeZXMayor.GetProperty("ExtraGold", BindingFlags.Public | BindingFlags.Instance);
                    _pMayorExtraWood = TypeZXMayor.GetProperty("ExtraWood", BindingFlags.Public | BindingFlags.Instance);
                    _pMayorExtraStone = TypeZXMayor.GetProperty("ExtraStone", BindingFlags.Public | BindingFlags.Instance);
                    _pMayorExtraIron = TypeZXMayor.GetProperty("ExtraIron", BindingFlags.Public | BindingFlags.Instance);
                    _pMayorExtraOil = TypeZXMayor.GetProperty("ExtraOil", BindingFlags.Public | BindingFlags.Instance);
                    _pMayorNameProp = TypeZXMayor.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    _pMayorIdProp = TypeZXMayor.GetProperty("ID", BindingFlags.Public | BindingFlags.Instance);
                    _pMayorLevelProp = TypeZXMayor.GetProperty("Level", BindingFlags.Public | BindingFlags.Instance);
                    _pMayorQuoteProp = TypeZXMayor.GetProperty("Quote", BindingFlags.Public | BindingFlags.Instance);
                    _pMayorActiveProp = TypeZXMayor.GetProperty("Active", BindingFlags.Public | BindingFlags.Instance);
                    _pMayorCustomProp = TypeZXMayor.GetProperty("Custom", BindingFlags.Public | BindingFlags.Instance);
                }

                Check(TypeZXMapGenerator, "Type RandomMapGenerator");
                Check(_methodMapGeneratorGenerate, "RandomMapGenerator.Generate(ZXRandomLevelParams)");
                Check(TypeCommandCenter, "Type ZX.Entities.CommandCenter");
                Check(TypeRanger, "Type ZX.Entities.Ranger");
                Check(TypeSoldierRegular, "Type ZX.Entities.SoldierRegular");
                Check(_typeZXRandomLevelParams, "Type ZXRandomLevelParams");
                Check(_propRandomParamsNCells, "ZXRandomLevelParams.NCells");
                Check(_propRandomParamsFactorDuration, "ZXRandomLevelParams.FactorGameDuration");
                Check(_propRandomParamsFactorZombiePop, "ZXRandomLevelParams.FactorZombiePopulation");
                Check(_typeZXMapTheme, "Type ZXMapTheme");
                Check(_methodZXMapThemeGetThemes, "ZXMapTheme.GetThemes");
                Check(_propMapThemeDoomVillagesSize, "ZXMapTheme.DoomVillagesSize");
                Check(_propMapThemeNumTreasures, "ZXMapTheme.NumTreasures");
                Check(_propMapThemeNumSuppyFood, "ZXMapTheme.NumSuppyFood");
                Check(_propMapThemeNumSuppyEnergy, "ZXMapTheme.NumSuppyEnergy");
                Check(_propMapThemeNumInfectedGiants, "ZXMapTheme.NumInfectedGiants");
                Check(_propMapThemeNumInfectedMutants, "ZXMapTheme.NumInfectedMutants");
                Check(_propMapThemeInfectedFactor, "ZXMapTheme.InfectedFactor");
                Check(_typeZXLevelExtension, "Type ZXLevelExtension");
                Check(_propLevelExtMapDrawer, "ZXLevelExtension.MapDrawer");
                Check(_propLevelExtLevelEvents, "ZXLevelExtension.LevelEvents");
                Check(_propLevelExtHideEnemies, "ZXLevelExtension.HideEnemiesIfNotWatched");
                Check(_methodLevelExtGetCurrent, "ZXLevelExtension.Current");
                Check(TypeCInvisibleWhenNotWatched, "Type CInvisibleWhenNotWatched");
                Check(_methodCInvisibleUpdateOpacity, "CInvisibleWhenNotWatched.UpdateOpacity");
                Check(_typeZXMapDrawer, "Type ZXMapDrawer");
                Check(_propMapDrawerExtraEntities, "ZXMapDrawer.ExtraEntities");
                Check(_propMapDrawerLayerTerrain, "ZXMapDrawer.LayerTerrain");
                Check(_propMapDrawerLayerObjects, "ZXMapDrawer.LayerObjects");
                Check(_propMapDrawerNCells, "ZXMapDrawer.NCells");
                Check(_typeZXLevelEvent, "Type ZXLevelEvent");
                Check(_methodLevelEventScalePop, "ZXLevelEvent.ScalePopulation");
                Check(_propLevelEventStartTimeH, "ZXLevelEvent.StartTimeH");
                Check(_propLevelEventGameTimeH, "ZXLevelEvent.GameTimeH");
                Check(_propLevelEventFinalSwarm, "ZXLevelEvent.FinalSwarm");
                Check(_propLevelEventAllInfectedToCC, "ZXLevelEvent.AllInfectedToCommandCenter");
                Check(_propLevelStateExtraWorkers, "ZXLevelState.CommandCenterExtraWorkers");
                Check(_propLevelStateExtraFood, "ZXLevelState.CommandCenterExtraFood");
                Check(_methodLevelStateRecalcResources, "ZXLevelState.RecalcResources");
                Check(_propGameStateRandom, "ZXGameState.Random");
                Check(_methodLevelSysCapturePanorama, "ZXSystemGameLevel.CapturePanorama");
                Check(_methodLevelSysEnterGameView, "ZXSystemGameLevel.EnterGameView");
                Check(_propLevelStateEndStatus, "ZXLevelState.EndStatus");
                Check(_fieldLevelSysGameEnded, "ZXSystemGameLevel.GameEndedFlag");
                Check(_fieldLevelSysScreenAnim, "ZXSystemGameLevel.ScreenAnim");
                Check(_methodLevelSysTriggerGameEnd, "ZXSystemGameLevel.TriggerGameEnd");
                Check(_methodLevelSysShowSurvivalEnd, "ZXSystemGameLevel.ShowSurvivalEnd");
                Check(_methodLevelSysShowCampaignEnd, "ZXSystemGameLevel.ShowCampaignEnd");
                Check(_methodGameShowMayorSelection, "ZXGame.ShowMayorSelection");
                Check(_typeZXGameState, "Type ZXGameState");
                Check(_methodLevelSysRefreshSelectionGroupsHud, "ZXSystemGameLevel.RefreshSelectionGroupsHud");
                Check(_methodLevelStateGetSelectionGroups, "ZXLevelState.GetSelectionGroups");
                Check(_typeKeyboardBindingsManager, $"Type KeyboardBindingsManager ({ObfuscatedSymbols.Type_KeyboardBindingsManager})");
                Check(_methodKeyboardIsKeyForCommand, "KeyboardBindingsManager.IsKeyForCommand");
                Check(_fieldLevelSysBuildAreaCursor, "ZXSystemGameLevel.BuildAreaCursor");
                Check(_typeOCellAreaCursor, "Type ZX.GUI.OCellAreaCursor");
                Check(_fieldCursorLastEnergyCell, "OCellAreaCursor.LastEnergyCell");
                Check(_methodCursorClearEnergyPreview, "OCellAreaCursor.ClearEnergyPreview");
                Check(_methodCursorUpdateVisual, "OCellAreaCursor.UpdateVisual");
                Check(_typeZXOrder, "Type ZX.Commands.ZXOrder");
                Check(_ctorZXOrder, "ZXOrder..ctor(ZXCommand, ZXCommandTarget)");
                Check(_typeCBuildable, "Type ZX.Components.CBuildable");
                Check(_propCBuildableFactor, "CBuildable.BuildingFactor");
                Check(_propCBuildableMode, "CBuildable.BuildMode");
                Check(_propStructureIsBeingBuilt, "Structure.IsBeingBuilt");
                Check(_methodBuildingRecalcResources, "Building.RecalcResources");
                Check(_methodStructureUpdateEnabled, "Structure.UpdateEnabled");
                Check(_methodLevelStateRecordEntityCreated, "ZXLevelState.RecordEntityCreated");
                Check(_methodLevelSysRefreshCmdPanel, "ZXSystemGameLevel.RefreshCmdPanel");
                Check(_propEntityTeam, "ZXEntity.Team");
                Check(_propEntityRelation, "ZXEntity.Relation");
                Check(_propMovableTargetPosProp, "CMovable.TargetPosition");
                Check(_propMovablePath, "CMovable.Path");
                Check(_fieldMovablePendingPath, "CMovable.PendingPathRequest");
                Check(_typeZXSystemMapLayers, "Type ZXSystemMapLayers");
                Check(_fieldMapLayersCurrent, "ZXSystemMapLayers.Current");
                Check(_fieldMapLayersEnergyGrid, "ZXSystemMapLayers.EnergyGrid");
                Check(_fieldMapLayersEnergyFrontier, "ZXSystemMapLayers.EnergyFrontier");
                Check(_fieldMapLayersEnergySources, "ZXSystemMapLayers.EnergySources");
                Check(_fieldMapLayersEnergyLastIdx, "ZXSystemMapLayers.EnergyLastIdx");
                Check(_fieldMapLayersEnergyInitDone, "ZXSystemMapLayers.EnergyInitDone");
                Check(_fieldMapLayersEnergyLock, "ZXSystemMapLayers.EnergyLock");
                Check(_propMapLayersNEnergyChanges, "ZXSystemMapLayers.NEnergyMapChanges");
                Check(_propMapLayersLayerEnergyUpdated, "ZXSystemMapLayers.LayerEnergyUpdated");
                Check(_methodMapLayersRebuildEnergyGrid, "ZXSystemMapLayers.RebuildEnergyGrid");
                Check(_methodMapLayersHasPathToAnyCC, "ZXSystemMapLayers.HasPathToAnyCC");
                Check(_methodMapLayersHasPathBetween, "ZXSystemMapLayers.HasPathBetween");
                Check(TypeCEnergyTransferer, "Type CEnergyTransferer");
                Check(_methodEnergyTransfererUpdateGrid, "CEnergyTransferer.UpdateGrid");
                Check(_fieldBuildingHasEnergyConn, "Building.HasEnergyConnection");
                Check(_methodBuildingRefreshEnergy, "Building.RefreshEnergy");
                Check(_methodLevelSysIsCellPlayableForEnergy, "ZXSystemGameLevel.IsCellPlayableForEnergy");
                Check(_typeZXComboBox, "Type ZX.GUI.ZXComboBox");
                Check(TypeCNeutralJoinable, "Type CNeutralJoinable");
                Check(TypeCLife, "Type CLife");
                Check(TypeTrain, "Type ZX.Commands.Train");
                Check(TypeUpgrade, "Type ZX.Commands.Upgrade");
                Check(_fieldLevelStateCachedCC, "ZXLevelState.CachedCC");
                Check(_methodLevelStateRefreshCCCell, "ZXLevelState.RefreshCCCell");
                Check(_propLevelStateCurrentCCCell, "ZXLevelState.CurrentCommandCenterCell");
                Check(_propLevelStateInfectedDestiny, "ZXLevelState.DefaultCellInfectedDestiny");
                Check(_propLevelStateScriptVars, "ZXLevelState.ScriptVariables");
                Check(_fieldLevelSysCurrentCC, "ZXSystemGameLevel.CurrentCommandCenter");
                Check(_propBuildingInfected, "Building.Infected");
                Check(_propDefaultParamsEnergyRadius, "ZXEntityDefaultParams.EnergyTransferRadius");
                Check(_typeBData, "Type ZX.Behaviours.BData");
                Check(_methodBDataGetOwner, "BData.GetOwner");
                Check(_propBDataTargetPos, "BData.TargetPosition");
                Check(TypeTechnology, "Type ZX.Commands.Technology");
                Check(TypeUndoBuilding, "Type ZX.Commands.UndoBuilding");
                Check(TypeDestroy, "Type ZX.Commands.Destroy");
                Check(TypeCResourceGenerator, "Type ZX.Components.CResourceGenerator");
                Check(TypePickableItem, "Type ZX.Entities.PickableItem");
                Check(TypeTheGreatTelescope, "Type ZX.Entities.TheGreatTelescope");
                Check(TypeTheAcademy, "Type ZX.Entities.TheAcademy");
                Check(TypeTheSpire, "Type ZX.Entities.TheSpire");
                Check(TypeWoodWorkshop, "Type ZX.Entities.WoodWorkshop");
                Check(TypeStoneWorkshop, "Type ZX.Entities.StoneWorkshop");
                Check(TypeFoundry, "Type ZX.Entities.Foundry");
                Check(TypeTheCrystalPalace, "Type ZX.Entities.TheCrystalPalace");
                Check(TypeTheTransmutator, "Type ZX.Entities.TheTransmutator");
                Check(TypeTheVictorious, "Type ZX.Entities.TheVictorious");
                Check(TypeTechGreatTelescope, "Type ZX.Commands.TechTheGreatTelescope");
                Check(TypeTechSoldierAcademy, "Type ZX.Commands.TechTheSoldierAcademy");
                Check(TypeTechElectricSpire, "Type ZX.Commands.TechTheElectricSpire");
                Check(TypeZXResourcesCost, "Type ZX.ZXResourcesCost");
                Check(TypeZXMayor, "Type ZX.ZXMayor");
                Check(_fLsWood, "ZXLevelState.Wood field");
                Check(_fLsGold, "ZXLevelState.Gold field");
                Check(_fLsStorage, "ZXLevelState.ResourceStorage field");
                Check(_pLsOnlyOneCmds, "ZXLevelState.OnlyOneExecutedCommandsFinished");
                Check(_mLsRecalculate, "ZXLevelState.Recalculate");
                Check(_mLsAddWoodMarket, "ZXLevelState.AddWoodWithMarket");
                Check(_mLsAddGoldDirect, "ZXLevelState.AddGoldDirect");
                Check(_mLsConsumeCost, "ZXLevelState.ConsumeCost");
                Check(_mLsAddCost, "ZXLevelState.AddCost");
                Check(_mResCostForType, "ZXResourcesCost.ForEntityType");
                Check(_pCcTelescopeEnabled, "CommandCenter.TelescopeEnabled");
                Check(_fBldgResCost, "Building.ResourceCost field");
                Check(_pDefMaxInstances, "ZXEntityDefaultParams.MaxInstances");
                Check(_pDefMinColonists, "ZXEntityDefaultParams.MinColonistsRequisite");
                Check(_pDefBuildingRequisite, "ZXEntityDefaultParams.BuildingRequisite");
                Check(_pDefWorkSupply, "ZXEntityDefaultParams.WorkersSupply");
                Check(_mCmdGetDefaultParams, "ZXCommand.GetDefaultParams");
                Check(_pCmdDefBuildingRequisite, "ZXCommandDefaultParams.BuildingRequisite");
                Check(_fCmdErrorMsg, "ZXCommand.ErrorMsg");
                Check(_pBldgOnlyOnceCmdsExecuted, "Building.OnlyOnceCommandsExecuted");
                Check(_mCmdGetCost, "ZXCommand.GetCost");
                Check(_propMapThemeNumDoomVillages, "ZXMapTheme.NumDoomVillages");
                Check(_propMapDrawerLayerZombies, "ZXMapDrawer.LayerZombies");
                Check(_propLevelEventNTimesTriggered, "ZXLevelEvent.NTimesTriggered");
                Check(_propLevelEventShowCountdown, "ZXLevelEvent.ShowCountdown");
                Check(_propLevelEventMaxRepetitions, "ZXLevelEvent.MaxRepetitions");
                Check(_propLevelEventAttackCC, "ZXLevelEvent.AttackCommandCenter");
                Check(_propLevelEventCurrentUnitGenerators, "ZXLevelEvent.CurrentUnitGenerators");
                Check(_typeCUnitGenerator, "Type ZX.Components.CUnitGenerator");
                Check(_propUnitGenConfigName, "CUnitGenerator.Configuration.Name");
                Check(_propGenReqIDEvent, "GenerationRequest.IDEvent");
                Check(_propGenReqCount, "GenerationRequest.Count");
                Check(_propGenReqGenSpeed, "GenerationRequest.GenerationSpeed");
                Check(_methodGenReqGetTemplate, "GenerationRequest.GetTemplate");
                Check(_typeCInfectionNest, "Type ZX.Components.CInfectionNest");
                Check(_propInfectionNestFactor, "CInfectionNest.Configuration.InfectedFactor");
                Check(_typeCMiniMapInfo, "Type ZX.Components.CMiniMapInfo");
                Check(_propZombieCellsAwayRoaming, "Zombie.CellsAwayCommandCenterWhenRoaming");
                Check(TypeZombieGiant, "Type ZX.Entities.ZombieGiant");
                Check(TypeZombieMutant, "Type ZX.Entities.ZombieMutant");
                Check(TypeZombieStrongA, "Type ZX.Entities.ZombieStrongA");
                Check(TypeZombieVenom, "Type ZX.Entities.ZombieVenom");
                Check(TypeZombieHarpy, "Type ZX.Entities.ZombieHarpy");
                Check(_methodCatalogDoomNestLarge, "Catalog.DoomNestLarge");
                Check(_methodCatalogDoomNestMedium, "Catalog.DoomNestMedium");
                Check(_methodCatalogDoomNestSmall, "Catalog.DoomNestSmall");

                if (missingCount == 0)
                {
                    TryCompileFastCommandDelegates();
                    TryCompileFastPerfDelegates();
                    ModLogger.InfoImmediate($"[GameBridge] ALL BINDINGS VERIFIED SUCCESSFULLY (0 missing, verified={verifiedCount}).");
                    IsInitialized = true;
                    return true;
                }
                else
                {
                    ModLogger.Error($"[GameBridge] Binding validation failed with {missingCount} missing symbol(s)!");
                    return false;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] Exception during InitializeAndValidate", ex);
                return false;
            }
        }

        // ====================================================================
        // Public Strongly-Typed Wrapper API
        // ====================================================================

        public static DXGame ZXGameInstance => _mZXGameCurrent?.Invoke(null, null) as DXGame;
        public static DXSystem LevelSystemInstance => _mLevelSysCurrent?.Invoke(null, null) as DXSystem;
        public static DXSystem MainMenuInstance => _mMainMenuCurrent?.Invoke(null, null) as DXSystem;
        /// <summary>ZXGameState is internal; expose it as object so callers can reflect safely.</summary>
        public static object CurrentGameStateObject => _mGameStateCurrent?.Invoke(null, null);

        private static readonly Dictionary<string, PropertyInfo> _gameStatePropCache = new Dictionary<string, PropertyInfo>();

        public static object GetGameStateProperty(string name)
        {
            object gs = CurrentGameStateObject;
            if (gs == null) return null;
            if (!_gameStatePropCache.TryGetValue(name, out PropertyInfo prop))
            {
                prop = gs.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                _gameStatePropCache[name] = prop;
            }
            try { return prop?.GetValue(gs); } catch { return null; }
        }

        /// <summary>
        /// ZXLevelState is an internal type; a direct cast fails at runtime with TypeAccessException.
        /// Everything touching it must go through reflection, so it is returned as object.
        /// </summary>
        public static object CurrentLevelState => _mLevelStateCurrent?.Invoke(null, null);
        public static Type LevelStateType => _mLevelStateCurrent?.ReturnType;

        private static readonly Dictionary<string, PropertyInfo> _levelPropCache = new Dictionary<string, PropertyInfo>();

        public static PropertyInfo GetLevelProperty(string name)
        {
            if (_levelPropCache.TryGetValue(name, out var cached)) return cached;
            PropertyInfo prop = LevelStateType?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            _levelPropCache[name] = prop;
            return prop;
        }

        public static int GetLevelInt(string name)
        {
            var prop = GetLevelProperty(name);
            var ls = CurrentLevelState;
            if (prop == null || ls == null) return 0;
            try { return Convert.ToInt32(prop.GetValue(ls)); } catch { return 0; }
        }

        public static bool SetLevelProperty(string name, object value)
        {
            var prop = GetLevelProperty(name);
            var ls = CurrentLevelState;
            if (prop == null || ls == null || !prop.CanWrite) return false;
            try { prop.SetValue(ls, value); return true; } catch { return false; }
        }

        /// <summary>
        /// Applies an integer delta to a ZXLevelState resource property (Gold/Wood/Stone/Iron/Oil)
        /// via its private setter so engine storage clamping (DXMath.Limit) is respected.
        /// </summary>
        public static bool AddLevelResourceDelta(string name, int delta)
        {
            if (delta == 0) return true;
            var prop = GetLevelProperty(name);
            var ls = CurrentLevelState;
            if (prop == null || ls == null) return false;
            var setter = prop.GetSetMethod(nonPublic: true);
            if (setter == null) return false;
            try
            {
                int cur = Convert.ToInt32(prop.GetValue(ls));
                int next = cur + delta;
                if (!string.Equals(name, "Gold", StringComparison.Ordinal) && next < 0)
                    next = 0;
                setter.Invoke(ls, new object[] { next });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reveals the whole map via ZXLevelState.ShowFullMap, which is OR'ed into every visibility
        /// check in ZXSystemMapLayers. Rendering-only — it does not mutate the simulation state.
        /// </summary>
        public static bool SetShowFullMap(bool on) => SetLevelProperty("ShowFullMap", on);

        private static Type _mapLayersType;
        private static FieldInfo _mapLayersCurrentField;
        private static PropertyInfo _fogEnabledProp;
        private static PropertyInfo _shadowEnabledProp;

        private static bool SetMapLayersBoolProperty(ref PropertyInfo propCache, string propName, bool on)
        {
            object inst = GetMapLayersInstance();
            if (inst == null) return false;
            if (propCache == null)
                propCache = _mapLayersType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (propCache == null || !propCache.CanWrite) return false;
            try { propCache.SetValue(inst, on); return true; } catch { return false; }
        }

        /// <summary>Toggles ZXSystemMapLayers.FogEnabled (the global fog-of-war switch).</summary>
        public static bool SetFogEnabled(bool on) => SetMapLayersBoolProperty(ref _fogEnabledProp, "FogEnabled", on);

        /// <summary>Toggles ZXSystemMapLayers.ShadowEnabled (the global dynamic shadow-of-war switch).</summary>
        public static bool SetShadowEnabled(bool on) => SetMapLayersBoolProperty(ref _shadowEnabledProp, "ShadowEnabled", on);

        /// <summary>
        /// Checks whether <paramref name="key"/> matches the player's currently active game
        /// keyboard binding (Standard / WASD / Custom) for <paramref name="command"/>.
        /// </summary>
        public static bool IsKeyBoundToCommand(DXKeys key, ZXKeyboardCommand command, DXKeys fallbackKey = DXKeys.None)
        {
            if (_methodKeyboardIsKeyForCommand != null)
            {
                try
                {
                    return (bool)_methodKeyboardIsKeyForCommand.Invoke(null, new object[] { key, command });
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[GameBridge] IsKeyBoundToCommand failed for {command}: {ex.Message}");
                }
            }
            return fallbackKey != DXKeys.None && key == fallbackKey;
        }

        /// <summary>
        /// Captures the local player's 1..8 (9-slot) control groups as deterministic entity IDs
        /// before an in-game snapshot reload replaces ZXLevelState.
        /// </summary>
        public static ulong[][] CaptureSelectionGroupEntityIds()
        {
            try
            {
                object ls = CurrentLevelState;
                if (ls == null || _methodLevelStateGetSelectionGroups == null) return null;
                var groups = _methodLevelStateGetSelectionGroups.Invoke(ls, null) as IList;
                if (groups == null || groups.Count == 0) return null;

                var captured = new ulong[groups.Count][];
                for (int i = 0; i < groups.Count; i++)
                {
                    var slot = groups[i] as IEnumerable;
                    if (slot == null)
                    {
                        captured[i] = Array.Empty<ulong>();
                        continue;
                    }

                    var ids = new List<ulong>();
                    foreach (object item in slot)
                    {
                        if (item is DXEntity ent && !ent.Disposed && (ent.ID & 0x8000000000000000UL) == 0)
                            ids.Add(ent.ID);
                    }
                    captured[i] = ids.ToArray();
                }
                return captured;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] CaptureSelectionGroupEntityIds failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Restores previously captured local control-group entity IDs onto the newly loaded
        /// ZXLevelState and refreshes the bottom-left control-group HUD slots.
        /// </summary>
        public static void RestoreSelectionGroupEntityIds(ulong[][] savedGroups)
        {
            if (savedGroups == null) return;
            try
            {
                var game = ZXGameInstance;
                object ls = CurrentLevelState;
                if (game == null || ls == null || _methodLevelStateGetSelectionGroups == null) return;

                var groups = _methodLevelStateGetSelectionGroups.Invoke(ls, null) as IList;
                if (groups == null) return;

                var filter = LocalEntityControlFilter;
                int slotCount = Math.Min(savedGroups.Length, groups.Count);
                int restoredTotal = 0;
                for (int i = 0; i < slotCount; i++)
                {
                    var list = groups[i] as IList;
                    if (list == null) continue;
                    list.Clear();

                    ulong[] ids = savedGroups[i];
                    if (ids == null || ids.Length == 0) continue;

                    for (int j = 0; j < ids.Length; j++)
                    {
                        DXEntity ent = game.GetEntityByID(ids[j]);
                        // Never reference internal ZXEntity directly at runtime (CLR 4.6 TypeAccessException);
                        // check via TypeZXEntity reflection and enforce local ownership filter.
                        if (ent != null && !ent.Disposed
                            && (TypeZXEntity == null || TypeZXEntity.IsInstanceOfType(ent))
                            && IsPlayerEntity(ent)
                            && (filter == null || filter(ent))
                            && !list.Contains(ent))
                        {
                            list.Add(ent);
                            restoredTotal++;
                        }
                    }
                }

                object levelSys = LevelSystemInstance;
                if (levelSys != null && _methodLevelSysRefreshSelectionGroupsHud != null)
                {
                    _methodLevelSysRefreshSelectionGroupsHud.Invoke(levelSys, null);
                }
                ModLogger.Info($"[GameBridge] Restored {restoredTotal} entity reference(s) across {slotCount} local control-group slot(s).");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] RestoreSelectionGroupEntityIds failed: {ex.Message}");
            }
        }

        public static string Localize(string key) => _mLocalize != null ? (string)_mLocalize.Invoke(null, new object[] { key }) : key;
        public static string GetSavesDirectory() => (string)_mGetSavesDir?.Invoke(null, null);

        public static string CurrentGameStateName
        {
            get
            {
                try
                {
                    var gs = CurrentGameStateObject;
                    if (gs == null || _typeZXGameState == null) return null;
                    return AccessTools.Property(_typeZXGameState, "Name")?.GetValue(gs, null) as string;
                }
                catch { return null; }
            }
            set
            {
                try
                {
                    var gs = CurrentGameStateObject;
                    if (gs == null || _typeZXGameState == null) return;
                    var prop = AccessTools.Property(_typeZXGameState, "Name");
                    if (prop != null && prop.CanWrite)
                        prop.SetValue(gs, value, null);
                }
                catch { }
            }
        }

        public static void WriteSaveFile(string fullFilePath)
        {
            var game = _mZXGameCurrent.Invoke(null, null);
            var ls = CurrentLevelState;
            if (ls != null)
            {
                if (Sync.CoopSessionManager.IsMultiFactionActive || Sync.FactionOwnershipTracker.IsInitialized)
                    Sync.FactionOwnershipTracker.SaveToLevelScriptVariables();
                AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_ZXLevelState_SaveTimeState)?.Invoke(ls, null);
            }
            _methodWriteSaveFile.Invoke(game, new object[] { fullFilePath });
        }

        public static bool SaveCurrentLevelAndExit(string saveName = null)
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                var gs = CurrentGameStateObject;
                if (game == null || gs == null || _methodSaveGameFlow == null)
                    return false;

                string chosenName = !string.IsNullOrWhiteSpace(saveName) ? saveName.Trim() : CurrentGameStateName;
                if (string.IsNullOrWhiteSpace(chosenName) || string.Equals(chosenName, "coop_host_snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    string orig = Sync.CoopSessionManager.HostOriginalSaveName;
                    chosenName = !string.IsNullOrWhiteSpace(orig) && !string.Equals(orig, "coop_host_snapshot", StringComparison.OrdinalIgnoreCase)
                        ? orig
                        : "CoopSave_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                }
                CurrentGameStateName = chosenName;

                if (DXModalScreen.IsInModal)
                    DXModalScreen.CloseAll();

                Action onComplete = () =>
                {
                    try { ReturnToMainMenu(); }
                    catch (Exception ex) { ModLogger.Error("[GameBridge] SaveCurrentLevelAndExit ReturnToMainMenu failed", ex); }
                };

                ModLogger.InfoImmediate($"[GameBridge] Triggering vanilla SaveGameFlow('{chosenName}') and exiting to MainMenu...");
                _methodSaveGameFlow.Invoke(game, new object[] { chosenName, onComplete, false, true });
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] SaveCurrentLevelAndExit failed", ex);
                return false;
            }
        }

        public static void LoadSaveFileAndEnter(string fileNameOrPath)
        {
            var game = _mZXGameCurrent.Invoke(null, null);
            _methodLoadGameAndEnter.Invoke(game, new object[] { fileNameOrPath });
        }

        /// <summary>
        /// Checks ZXGame's own save-file validity gate (the private static bool that
        /// LoadGameAndEnter consults at its very top). A false result makes that call return
        /// silently in ~5 ms with no exception and no loading — the exact symptom we chase.
        /// </summary>
        public static bool IsSaveLoadable(string path)
        {
            try
            {
                var m = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_IsSaveLoadable, new[] { typeof(string) });
                if (m == null) return true; // cannot check; let the engine decide
                return (bool)m.Invoke(null, new object[] { path });
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] IsSaveLoadable check failed: {ex.Message}");
                return true;
            }
        }

        private static MethodInfo _methodAnimate;
        private static int _kickFadeDiag;
        private static PropertyInfo _propZXGameIsLoading;

        public static bool IsGameLoading()
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return false;
                if (_propZXGameIsLoading == null)
                    _propZXGameIsLoading = AccessTools.Property(TypeZXGame, "IsLoading");
                return _propZXGameIsLoading != null && (bool)_propZXGameIsLoading.GetValue(game, null);
            }
            catch { return false; }
        }

        /// <summary>
        /// Advances the engine's black loading-fade DXObject by hand. LoadGameAndEnter gates the real
        /// load behind three DXAnimation fades (see ZXGame.#=zXAMaTPI=); if the scene renders the fade
        /// object but never advances its animation (unfocused/background window, no scene frames), the
        /// callback chain never fires and the level never loads. Driving the animation keeps the
        /// engine's own state machine intact instead of skipping callbacks.
        /// </summary>
        public static void KickLoadingFade(double advanceMs = 700.0)
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_LoadingFadeObj);
                if (!(field?.GetValue(game) is DXObject fade))
                {
                    if (++_kickFadeDiag <= 5) ModLogger.Info("[GameBridge] KickLoadingFade: fade object is null.");
                    return;
                }
                bool inScene = fade.Scene != null;
                bool hasAnim = fade.Animation != null;
                if (++_kickFadeDiag <= 5)
                    ModLogger.Info($"[GameBridge] KickLoadingFade: inScene={inScene} hasAnimation={hasAnim} opacity={fade.Opacity:F2} visible={fade.Visible}.");

                if (!inScene || !hasAnim) return;
                if (_methodAnimate == null)
                    _methodAnimate = AccessTools.Method(typeof(DXObject), "Animate", new[] { typeof(double) });
                _methodAnimate?.Invoke(fade, new object[] { advanceMs });
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] KickLoadingFade failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures ZXGame's fullscreen loading-fade object (#=zbvwh$ickzalV, MouseClickInteractionEnabled=true,
        /// Z=2147483642) is removed from the active scene once loading completes so it cannot swallow mouse
        /// clicks or leave a translucent overlay over the level.
        /// </summary>
        public static void ClearLoadingFadeOverlay()
        {
            try
            {
                if (IsGameLoading()) return;
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_LoadingFadeObj);
                if (field?.GetValue(game) is DXObject fade && fade.Scene != null)
                {
                    fade.Animation = null;
                    fade.Opacity = 0f;
                    fade.Visible = false;
                    fade.RemoveFromScene();
                    ModLogger.Info("[GameBridge] Cleared lingering loading-fade overlay after level load.");
                }
                // Removing the fade object aborts its fade-out animation callback that would otherwise clear
                // StartupFadeActive (#=zTkvm$fc5hNvM); reset it explicitly so future resync loads are not gated.
                var startupFadeField = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_StartupFadeActive);
                if (startupFadeField?.GetValue(game) is bool sfa && sfa)
                    startupFadeField.SetValue(game, false);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] ClearLoadingFadeOverlay failed: {ex.Message}");
            }
        }

        public static bool IsLoadingFadeInScene()
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return false;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_LoadingFadeObj);
                return field?.GetValue(game) is DXObject fade && fade.Scene != null;
            }
            catch { return false; }
        }

        public static DXSystem CurrentGameSystem
        {
            get
            {
                try
                {
                    var game = _mZXGameCurrent?.Invoke(null, null);
                    if (game == null || TypeZXGame == null) return null;
                    return AccessTools.Property(TypeZXGame, "CurrentGameSystem")?.GetValue(game) as DXSystem;
                }
                catch { return null; }
            }
        }

        public static bool IsMainMenuReadyForInput()
        {
            try
            {
                var menu = MainMenuInstance;
                if (menu == null) return false;
                var field = AccessTools.Field(TypeZXSystemMainMenu, ObfuscatedSymbols.Field_MainMenu_ReadyForInput);
                return field != null && (bool)field.GetValue(menu);
            }
            catch { return false; }
        }

        public static bool IsStartupFadeActive()
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return false;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_StartupFadeActive);
                return field != null && (bool)field.GetValue(game);
            }
            catch { return false; }
        }

        /// <summary>
        /// ZXGame.#=z9DPDdq9qP9lZ silently returns when its black loading-fade object is already in
        /// the scene (e.g. a main-menu transition still animating), so a programmatic load during a
        /// snapshot arrival becomes a no-op. Also works around vanilla LoadGameAndEnter line 229's
        /// inverted null check (`if (CurrentGameSystem == null) CurrentGameSystem.Enabled = false;`),
        /// which otherwise leaves an active ZXSystemGameLevel updating disposed CShadow scene objects
        /// after Scene.Init(Black) and throws NullReferenceException on in-level reloads.
        /// </summary>
        public static void PrepareForProgrammaticLoad()
        {
            try
            {
                if (DXModalScreen.IsInModal)
                    DXModalScreen.CloseAll();
                DisableCameraIntroAnimation();
                var game = _mZXGameCurrent?.Invoke(null, null) as DXGame;
                if (game == null) return;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_LoadingFadeObj);
                if (field?.GetValue(game) is DXObject fade && fade.Scene != null)
                {
                    fade.RemoveFromScene();
                    ModLogger.Info("[GameBridge] Detached stale loading-fade object before programmatic load.");
                }

                var sys = AccessTools.Property(TypeZXGame, "CurrentGameSystem")?.GetValue(game) as DXSystem;
                if (sys != null)
                {
                    bool wasInLevel = TypeZXSystemGameLevel != null && TypeZXSystemGameLevel.IsInstanceOfType(sys);
                    sys.Enabled = false;
                    game.Paused = true;
                    game.PostMethods_OnStartFrame?.Clear();
                    game.PostMethods_OnFinishFrame?.Clear();
                    game.PostMethods_OnSafeLogicAndDrawing?.Clear();
                    if (wasInLevel)
                    {
                        game.ClearEntities();
                        ModLogger.Info("[GameBridge] Disabled active level system and cleared entities before in-level reload.");
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] PrepareForProgrammaticLoad failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears any stuck Wait/Hourglass cursor lock left behind by interrupted async level loads
        /// or programmatic snapshot transitions so hover/click cursor updates resume normally.
        /// </summary>
        public static void ClearWaitCursor()
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null) as DXGame;
                if (game == null) return;

                var fWait = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_WaitCursorCount);
                int prevCount = 0;
                if (fWait != null)
                {
                    prevCount = (int)fWait.GetValue(game);
                    fWait.SetValue(game, 0);
                }

                bool wasLocked = game.CursorLocked;
                var mEnd = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_EndWaitCursor, Type.EmptyTypes);
                if (mEnd != null)
                {
                    mEnd.Invoke(game, null);
                }
                else
                {
                    game.CursorLocked = false;
                }

                game.CursorVisible = true;
                if (wasLocked || prevCount > 0)
                    ModLogger.Info($"[GameBridge] Cleared stuck WaitCursor (prevCount={prevCount}, wasLocked={wasLocked}).");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] ClearWaitCursor failed: {ex.Message}");
            }
        }

        public static void ShowTopBanner(string message, Color color, int durationMs = 3000)
        {
            var levelSys = _mLevelSysCurrent.Invoke(null, null);
            if (levelSys != null)
            {
                _methodShowTopBanner.Invoke(levelSys, new object[] { message, color, durationMs });
            }
        }

        public static bool OpenLevelGameMenu()
        {
            try
            {
                var levelSys = _mLevelSysCurrent?.Invoke(null, null);
                if (levelSys == null || _methodLevelSysOpenGameMenu == null) return false;
                _methodLevelSysOpenGameMenu.Invoke(levelSys, null);
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] OpenLevelGameMenu failed: {ex.Message}");
                return false;
            }
        }

        // NOTE: ZXCommand and ZXCommandTarget are internal types. Never cast to / `is`-check them here —
        // the Desktop CLR throws TypeAccessException on castclass/isinst of an inaccessible type
        // (铁律#3). All command execution paths therefore use object + reflection.
        public static object GetCommandById(int id) => _mCmdById.Invoke(null, new object[] { id });
        public static object GetCommandByName(string typeName) => _mCmdByName.Invoke(null, new object[] { typeName });

        private static Action<object, object, object> _fastCmdExec;
        private static Action<object, List<DXEntity>, object, PointF> _fastAssignGroupMove;
        private static Func<object, object> _fastGetCmdable;
        private static Type _fastGetCmdTargetDeclType;
        private static Func<object, object> _fastGetCmdTarget;
        private static Func<DXGame, List<DXEntity>> _fastGetGameEntities;
        private static Func<object, object> _fastGetLifeObj;
        private static Type _fastGetLifeIntDeclType;
        private static Func<object, int> _fastGetLifeInt;
        private static Type _fastGetOrdersQueuedDeclType;
        private static Func<object, IList> _fastGetOrdersQueued;
        private delegate void FastGetCombatAndMoveTargetsDelegate(object entity, out ulong targetEntityId, out float moveX, out float moveY);
        private static FastGetCombatAndMoveTargetsDelegate _fastGetCombatAndMoveTargets;

        private static void TryCompileFastCommandDelegates()
        {
            try
            {
                if (_mCmdExec != null)
                {
                    var dmExec = new DynamicMethod("TABCoop_FastCmdExec", typeof(void),
                        new[] { typeof(object), typeof(object), typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmExec.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, typeof(ZXCommand));
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Castclass, typeof(ZXEntity));
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(typeof(ZXCommandTarget).IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, typeof(ZXCommandTarget));
                    il.Emit(OpCodes.Callvirt, _mCmdExec);
                    il.Emit(OpCodes.Ret);
                    _fastCmdExec = (Action<object, object, object>)dmExec.CreateDelegate(typeof(Action<object, object, object>));
                }

                if (_methodAssignGroupMove != null && TypeZXSystemGameLevel != null)
                {
                    var dmGroup = new DynamicMethod("TABCoop_FastAssignGroupMove", typeof(void),
                        new[] { typeof(object), typeof(List<DXEntity>), typeof(object), typeof(PointF) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmGroup.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, TypeZXSystemGameLevel);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Castclass, typeof(ZXCommand));
                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Callvirt, _methodAssignGroupMove);
                    il.Emit(OpCodes.Ret);
                    _fastAssignGroupMove = (Action<object, List<DXEntity>, object, PointF>)dmGroup.CreateDelegate(
                        typeof(Action<object, List<DXEntity>, object, PointF>));
                }

                if (_mGetEntityCmd != null)
                {
                    var dmCmdable = new DynamicMethod("TABCoop_FastGetCommandable", typeof(object),
                        new[] { typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmCmdable.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, _mGetEntityCmd.DeclaringType);
                    il.Emit(OpCodes.Callvirt, _mGetEntityCmd);
                    il.Emit(OpCodes.Ret);
                    _fastGetCmdable = (Func<object, object>)dmCmdable.CreateDelegate(typeof(Func<object, object>));
                }

                Type ccmd = TypeCCommandable;
                var targetProp = ccmd?.GetProperty("Target", BindingFlags.Public | BindingFlags.Instance);
                var targetGetter = targetProp?.GetGetMethod(true);
                if (targetGetter != null)
                {
                    _fastGetCmdTargetDeclType = targetGetter.DeclaringType;
                    var dmTarget = new DynamicMethod("TABCoop_FastGetCmdTarget", typeof(object),
                        new[] { typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmTarget.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, _fastGetCmdTargetDeclType);
                    il.Emit(OpCodes.Callvirt, targetGetter);
                    if (targetGetter.ReturnType.IsValueType)
                        il.Emit(OpCodes.Box, targetGetter.ReturnType);
                    il.Emit(OpCodes.Ret);
                    _fastGetCmdTarget = (Func<object, object>)dmTarget.CreateDelegate(typeof(Func<object, object>));
                }

                var fEnt = typeof(DXGame).GetField("_Entities", BindingFlags.NonPublic | BindingFlags.Instance);
                if (fEnt != null && fEnt.FieldType == typeof(List<DXEntity>))
                {
                    var dmEnt = new DynamicMethod("TABCoop_FastGetGameEntities", typeof(List<DXEntity>),
                        new[] { typeof(DXGame) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmEnt.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, fEnt);
                    il.Emit(OpCodes.Ret);
                    _fastGetGameEntities = (Func<DXGame, List<DXEntity>>)dmEnt.CreateDelegate(typeof(Func<DXGame, List<DXEntity>>));
                }

                if (_mGetEntityLife != null)
                {
                    var dmLifeObj = new DynamicMethod("TABCoop_FastGetLifeObj", typeof(object),
                        new[] { typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmLifeObj.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, _mGetEntityLife.DeclaringType);
                    il.Emit(OpCodes.Callvirt, _mGetEntityLife);
                    il.Emit(OpCodes.Ret);
                    _fastGetLifeObj = (Func<object, object>)dmLifeObj.CreateDelegate(typeof(Func<object, object>));

                    var lifeGetter = _mGetEntityLife.ReturnType?.GetProperty("Life", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod(true);
                    if (lifeGetter != null && lifeGetter.ReturnType == typeof(int))
                    {
                        _fastGetLifeIntDeclType = lifeGetter.DeclaringType;
                        var dmLifeInt = new DynamicMethod("TABCoop_FastGetLifeInt", typeof(int),
                            new[] { typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
                        var ilI = dmLifeInt.GetILGenerator();
                        ilI.Emit(OpCodes.Ldarg_0);
                        ilI.Emit(OpCodes.Castclass, _fastGetLifeIntDeclType);
                        ilI.Emit(OpCodes.Callvirt, lifeGetter);
                        ilI.Emit(OpCodes.Ret);
                        _fastGetLifeInt = (Func<object, int>)dmLifeInt.CreateDelegate(typeof(Func<object, int>));
                    }
                }

                var ordersGetter = ccmd?.GetProperty("OrdersQueued", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod(true);
                if (ordersGetter != null && typeof(IList).IsAssignableFrom(ordersGetter.ReturnType))
                {
                    _fastGetOrdersQueuedDeclType = ordersGetter.DeclaringType;
                    var dmOrders = new DynamicMethod("TABCoop_FastGetOrdersQueued", typeof(IList),
                        new[] { typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmOrders.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, _fastGetOrdersQueuedDeclType);
                    il.Emit(OpCodes.Callvirt, ordersGetter);
                    il.Emit(OpCodes.Ret);
                    _fastGetOrdersQueued = (Func<object, IList>)dmOrders.CreateDelegate(typeof(Func<object, IList>));
                }

                if (_mGetEntityBehaviour != null && _mGetEntityMovable != null && _typeBData != null && _fieldBDataTargetRef != null && _propMovableTargetPosProp != null)
                {
                    Type tCBeh = _mGetEntityBehaviour.ReturnType;
                    var mGetBeh = tCBeh?.GetProperty("Behaviour", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod(true);
                    var mGetData = mGetBeh?.ReturnType?.GetProperty("Data", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)?.GetGetMethod(true);
                    var mGetRefId = _fieldBDataTargetRef.FieldType.GetProperty("IDEntity", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod(true);
                    var mGetMoveTarget = _propMovableTargetPosProp.GetGetMethod(true);
                    var mNullHasVal = typeof(PointF?).GetProperty("HasValue")?.GetGetMethod();
                    var mNullGetVal = typeof(PointF?).GetMethod("GetValueOrDefault", Type.EmptyTypes);
                    var mPtX = typeof(PointF).GetProperty("X")?.GetGetMethod();
                    var mPtY = typeof(PointF).GetProperty("Y")?.GetGetMethod();

                    if (mGetBeh != null && mGetData != null && mGetRefId != null && mGetMoveTarget != null
                        && mNullHasVal != null && mNullGetVal != null && mPtX != null && mPtY != null)
                    {
                        var dmTgt = new DynamicMethod(
                            "TABCoop_FastGetCombatAndMoveTargets",
                            typeof(void),
                            new[] { typeof(object), typeof(ulong).MakeByRefType(), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() },
                            typeof(GameBridge).Module,
                            skipVisibility: true);
                        var il = dmTgt.GetILGenerator();
                        var locZx = il.DeclareLocal(typeof(ZXEntity));
                        var locCBeh = il.DeclareLocal(tCBeh);
                        var locBeh = il.DeclareLocal(mGetBeh.ReturnType);
                        var locBData = il.DeclareLocal(_typeBData);
                        var locMov = il.DeclareLocal(_mGetEntityMovable.ReturnType);
                        var locOptPt = il.DeclareLocal(typeof(PointF?));
                        var locPt = il.DeclareLocal(typeof(PointF));
                        var lblMovable = il.DefineLabel();
                        var lblRet = il.DefineLabel();

                        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Stind_I8);
                        il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldc_R4, float.NaN); il.Emit(OpCodes.Stind_R4);
                        il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldc_R4, float.NaN); il.Emit(OpCodes.Stind_R4);

                        il.Emit(OpCodes.Ldarg_0);
                        il.Emit(OpCodes.Isinst, typeof(ZXEntity));
                        il.Emit(OpCodes.Stloc, locZx);
                        il.Emit(OpCodes.Ldloc, locZx);
                        il.Emit(OpCodes.Brfalse, lblRet);

                        il.Emit(OpCodes.Ldloc, locZx);
                        il.Emit(OpCodes.Callvirt, _mGetEntityBehaviour);
                        il.Emit(OpCodes.Stloc, locCBeh);
                        il.Emit(OpCodes.Ldloc, locCBeh);
                        il.Emit(OpCodes.Brfalse, lblMovable);

                        il.Emit(OpCodes.Ldloc, locCBeh);
                        il.Emit(OpCodes.Callvirt, mGetBeh);
                        il.Emit(OpCodes.Stloc, locBeh);
                        il.Emit(OpCodes.Ldloc, locBeh);
                        il.Emit(OpCodes.Brfalse, lblMovable);

                        il.Emit(OpCodes.Ldloc, locBeh);
                        il.Emit(OpCodes.Callvirt, mGetData);
                        if (!_typeBData.IsAssignableFrom(mGetData.ReturnType))
                            il.Emit(OpCodes.Isinst, _typeBData);
                        il.Emit(OpCodes.Stloc, locBData);
                        il.Emit(OpCodes.Ldloc, locBData);
                        il.Emit(OpCodes.Brfalse, lblMovable);

                        il.Emit(OpCodes.Ldarg_1);
                        il.Emit(OpCodes.Ldloc, locBData);
                        if (_fieldBDataTargetRef.FieldType.IsValueType)
                        {
                            il.Emit(OpCodes.Ldflda, _fieldBDataTargetRef);
                            il.Emit(OpCodes.Call, mGetRefId);
                        }
                        else
                        {
                            il.Emit(OpCodes.Ldfld, _fieldBDataTargetRef);
                            il.Emit(OpCodes.Dup);
                            var lblHasRef = il.DefineLabel();
                            il.Emit(OpCodes.Brtrue, lblHasRef);
                            il.Emit(OpCodes.Pop);
                            il.Emit(OpCodes.Pop);
                            il.Emit(OpCodes.Br, lblMovable);
                            il.MarkLabel(lblHasRef);
                            il.Emit(OpCodes.Callvirt, mGetRefId);
                        }
                        il.Emit(OpCodes.Stind_I8);

                        il.MarkLabel(lblMovable);
                        il.Emit(OpCodes.Ldloc, locZx);
                        il.Emit(OpCodes.Callvirt, _mGetEntityMovable);
                        il.Emit(OpCodes.Stloc, locMov);
                        il.Emit(OpCodes.Ldloc, locMov);
                        il.Emit(OpCodes.Brfalse, lblRet);

                        il.Emit(OpCodes.Ldloc, locMov);
                        il.Emit(OpCodes.Callvirt, mGetMoveTarget);
                        il.Emit(OpCodes.Stloc, locOptPt);
                        il.Emit(OpCodes.Ldloca, locOptPt);
                        il.Emit(OpCodes.Call, mNullHasVal);
                        il.Emit(OpCodes.Brfalse, lblRet);

                        il.Emit(OpCodes.Ldloca, locOptPt);
                        il.Emit(OpCodes.Call, mNullGetVal);
                        il.Emit(OpCodes.Stloc, locPt);
                        il.Emit(OpCodes.Ldarg_2);
                        il.Emit(OpCodes.Ldloca, locPt);
                        il.Emit(OpCodes.Call, mPtX);
                        il.Emit(OpCodes.Stind_R4);
                        il.Emit(OpCodes.Ldarg_3);
                        il.Emit(OpCodes.Ldloca, locPt);
                        il.Emit(OpCodes.Call, mPtY);
                        il.Emit(OpCodes.Stind_R4);

                        il.MarkLabel(lblRet);
                        il.Emit(OpCodes.Ret);

                        _fastGetCombatAndMoveTargets = (FastGetCombatAndMoveTargetsDelegate)dmTgt.CreateDelegate(typeof(FastGetCombatAndMoveTargetsDelegate));
                    }
                }

                ModLogger.Info("[Perf] Compiled DynamicMethods for fast command replay & state hashing.");
            }
            catch (Exception ex)
            {
                _fastCmdExec = null;
                _fastAssignGroupMove = null;
                _fastGetCmdable = null;
                _fastGetCmdTarget = null;
                _fastGetCmdTargetDeclType = null;
                _fastGetGameEntities = null;
                _fastGetLifeObj = null;
                _fastGetLifeIntDeclType = null;
                _fastGetLifeInt = null;
                _fastGetOrdersQueuedDeclType = null;
                _fastGetOrdersQueued = null;
                _fastGetCombatAndMoveTargets = null;
                ModLogger.Warn($"[Perf] TryCompileFastCommandDelegates failed (falling back to reflection): {ex.Message}");
            }
        }

        public static List<DXEntity> GetEntitiesList(DXGame game)
        {
            if (game == null || _fastGetGameEntities == null) return null;
            try { return _fastGetGameEntities(game); } catch { return null; }
        }

        public static bool IsZombieType(Type type) => type != null && _typeZombie != null && _typeZombie.IsAssignableFrom(type);

        public static bool IsColonistType(Type type) => type != null && TypeColonist != null && TypeColonist.IsAssignableFrom(type);

        public static bool IsStructureType(Type type) => type != null && TypeStructure != null && TypeStructure.IsAssignableFrom(type);

        public static void ExecuteCommandOnEntity(object cmd, object executor, object target)
        {
            if (_fastCmdExec != null)
            {
                _fastCmdExec(cmd, executor, target);
                return;
            }
            _mCmdExec.Invoke(cmd, new object[] { executor, target });
        }

        public static void AssignGroupMoveTargets(List<DXEntity> entities, object cmd, PointF destination)
        {
            var levelSys = _mLevelSysCurrent.Invoke(null, null);
            if (levelSys != null)
            {
                if (_fastAssignGroupMove != null)
                {
                    _fastAssignGroupMove(levelSys, entities, cmd, destination);
                    return;
                }
                _methodAssignGroupMove.Invoke(levelSys, new object[] { entities, cmd, destination });
            }
        }

        public static object PlaceBuilding(object builder, object target, Type buildingType, bool isBonus = false, bool instant = false, DXEntityTemplate template = null)
        {
            return _methodPlaceBuilding.Invoke(null, new object[] { builder, target, buildingType, isBonus, instant, template });
        }

        public static CSelectable GetSelectable(this ZXEntity entity) => (CSelectable)_mGetEntitySel.Invoke(entity, null);
        public static CLife GetLife(this ZXEntity entity) => (CLife)_mGetEntityLife.Invoke(entity, null);

        public static object GetCurrentCommandObject(object commandable) => _mCurCmd?.Invoke(commandable, null);
        public static void CancelCurrentOrderObject(object commandable) => _mCancelCur?.Invoke(commandable, null);
        public static void CancelQueueSlotObject(object commandable, int slotIndex) => _mCancelSlot?.Invoke(commandable, new object[] { slotIndex });

        private static PropertyInfo _propSelectedSetData;

        public static object GetLocallySelectedList()
        {
            return _fieldSelectedList?.GetValue(null);
        }

        private static IEnumerable EnumerateSelectedComponents()
        {
            object setObj = _fieldSelectedList?.GetValue(null);
            if (setObj == null) return Array.Empty<object>();
            if (_propSelectedSetData == null)
                _propSelectedSetData = setObj.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
            return (_propSelectedSetData?.GetValue(setObj, null) as IEnumerable) ?? Array.Empty<object>();
        }

        public static ZXEntity GetPrimarySelectedEntity() => (ZXEntity)_mPrimarySel.Invoke(null, null);
        public static void UnselectAllLocal() => _mUnselectAll.Invoke(null, null);

        public static DXTextRenderer GetPlainTextRenderer()
        {
            return (DXTextRenderer)_methodGetPlainTextRenderer.Invoke(null, null);
        }

        public static object ShowAlertMessageBox(string title, string message, Action onOk = null)
        {
            return _methodShowAlert.Invoke(null, new object[] { title, message, onOk });
        }

        public static object ShowInputBox(
            string title,
            string message,
            string defaultText,
            Action<string> onOk,
            Action onCancel = null,
            int maxLength = 64,
            Func<string, bool> validator = null)
        {
            if (_methodShowInputBox == null || TypeDXEditBoxTypeOfInput == null)
            {
                onOk?.Invoke(defaultText ?? string.Empty);
                return null;
            }

            try
            {
                // TypeOfInput.AllCharactersExceptNewLines = 0 (allows digits, dots, colons, IPv6 brackets).
                object inputType = Enum.ToObject(TypeDXEditBoxTypeOfInput, 0);
                return _methodShowInputBox.Invoke(null, new object[]
                {
                    title ?? string.Empty,
                    message ?? string.Empty,
                    defaultText ?? string.Empty,
                    inputType,
                    Math.Max(1, maxLength),
                    onOk ?? (s => { }),
                    onCancel ?? (() => { }),
                    validator ?? (s => true)
                });
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] ShowInputBox failed", ex);
                onOk?.Invoke(defaultText ?? string.Empty);
                return null;
            }
        }

        public static object ShowYesNoMessageBox(string title, string message, Action onYes, Action onNo = null)
        {
            return _methodShowYesNo.Invoke(null, new object[] { title, message, onYes, onNo });
        }

        public static object ShowListSelectorMessageBox(
            string title,
            string message,
            string[] options,
            Action<int> onOk,
            Action onCancel = null)
        {
            if (TypeZXWindowMessage == null || _methodShowListSelector == null)
                return ShowAlertMessageBox(title, message, onCancel);

            object msgObj = Activator.CreateInstance(TypeZXWindowMessage);
            _propWindowMsgTitle?.SetValue(msgObj, title, null);
            _propWindowMsgText?.SetValue(msgObj, message, null);
            return _methodShowListSelector.Invoke(null, new object[] { msgObj, onOk, onCancel, options });
        }

        // ====================================================================
        // Deterministic test-harness helpers.
        // NOTE: all game entity/component classes are internal, so these return base/public
        // types (DXEntity) and use reflection internally. Never cast to ZXEntity here.
        // ====================================================================

        private static readonly Dictionary<Type, PropertyInfo> _teamPropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _lifePropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _ordersPropCache = new Dictionary<Type, PropertyInfo>();
        private static PropertyInfo _ccProp;

        internal static int CompareEntitiesDeterministically(DXEntity a, DXEntity b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            bool aHigh = (a.ID & 0x8000000000000000UL) != 0UL;
            bool bHigh = (b.ID & 0x8000000000000000UL) != 0UL;
            if (aHigh != bHigh) return aHigh ? 1 : -1;

            if (!aHigh)
                return a.ID.CompareTo(b.ID);

            int cmp = a.Cell.X.CompareTo(b.Cell.X);
            if (cmp != 0) return cmp;
            cmp = a.Cell.Y.CompareTo(b.Cell.Y);
            if (cmp != 0) return cmp;
            cmp = a.Position.X.CompareTo(b.Position.X);
            if (cmp != 0) return cmp;
            cmp = a.Position.Y.CompareTo(b.Position.Y);
            if (cmp != 0) return cmp;
            cmp = (a.Template?.ID ?? 0UL).CompareTo(b.Template?.ID ?? 0UL);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(a.GetType().FullName, b.GetType().FullName);
            if (cmp != 0) return cmp;
            return a.ID.CompareTo(b.ID);
        }

        private static int CompareBodiesDeterministically(DXPhysicBody a, DXPhysicBody b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            int cmp = CompareEntitiesDeterministically(a.GameEntity, b.GameEntity);
            if (cmp != 0) return cmp;

            cmp = a.PositionComputed.X.CompareTo(b.PositionComputed.X);
            if (cmp != 0) return cmp;
            cmp = a.PositionComputed.Y.CompareTo(b.PositionComputed.Y);
            if (cmp != 0) return cmp;
            return ((int)a.Shape).CompareTo((int)b.Shape);
        }

        public static void SortEntitiesById()
        {
            try
            {
                var game = DXGame.Current;
                if (game == null) return;

                var entitiesField = AccessTools.Field(typeof(DXGame), "_Entities");
                if (entitiesField?.GetValue(game) is List<DXEntity> list && list.Count > 1)
                {
                    list.Sort(CompareEntitiesDeterministically);
                    var indexField = AccessTools.Field(typeof(DXEntity), "_Index");
                    if (indexField != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (list[i] != null)
                                indexField.SetValue(list[i], i);
                        }
                    }
                    ModLogger.Info($"[GameBridge] Re-ordered {list.Count} entities deterministically.");

                    // ClearEntities() clears HashSets in place without resetting bucket capacities, so an
                    // in-level reload leaves larger _buckets arrays than a fresh MainMenu load and makes
                    // EntitiesOfType<T>() iterate in a different hash-collision order (e.g. SysColonists).
                    // Rebuild _EntitiesPerType and _DicBaseTypesPerEntity from fresh HashSets in sorted order.
                    var perTypeField = AccessTools.Field(typeof(DXGame), "_EntitiesPerType");
                    var baseTypesField = AccessTools.Field(typeof(DXGame), "_DicBaseTypesPerEntity");
                    if (perTypeField?.GetValue(game) is Dictionary<Type, HashSet<DXEntity>> perType &&
                        baseTypesField?.GetValue(null) is Dictionary<Type, List<HashSet<DXEntity>>> baseTypes)
                    {
                        int rebuiltSets = 0;
                        lock (DXGame.SyncLogic)
                        {
                            perType.Clear();
                            baseTypes.Clear();
                            for (int i = 0; i < list.Count; i++)
                            {
                                var e = list[i];
                                if (e == null) continue;
                                Type et = e.GetType();
                                if (!baseTypes.TryGetValue(et, out var setList))
                                {
                                    setList = new List<HashSet<DXEntity>>();
                                    baseTypes.Add(et, setList);
                                    Type t = et;
                                    while (t != null && t != typeof(DXEntity))
                                    {
                                        if (!perType.TryGetValue(t, out var hs))
                                        {
                                            hs = new HashSet<DXEntity>();
                                            perType.Add(t, hs);
                                            rebuiltSets++;
                                        }
                                        setList.Add(hs);
                                        t = t.BaseType;
                                    }
                                }
                                for (int j = 0; j < setList.Count; j++)
                                    setList[j].Add(e);
                            }
                        }
                        ModLogger.Info($"[GameBridge] Rebuilt {rebuiltSets} _EntitiesPerType sets deterministically.");
                    }
                    InvalidateCommandCenterCache();
                }

                // Flush deferred DXEntity.CreateBody() calls queued during async level load, then sort
                // DXPhysicWorld.Bodies in place WITHOUT calling Clear() — InitLevel also registers ~358
                // static terrain/gate colliders directly via DXPhysicBody.CreateRectangle(userData:null
                // or gate sub-bodies) that CreateBody() does not recreate.
                AccessTools.Method(typeof(DXPhysicWorld), "FinishPhysics")?.Invoke(null, null);
                var bodies = DXPhysicWorld.Bodies;
                if (bodies != null && bodies.Count > 1)
                {
                    bodies.RemoveAll(b => b == null || b.Handle == IntPtr.Zero);
                    bodies.Sort(CompareBodiesDeterministically);
                    var bodyIndexField = AccessTools.Field(typeof(DXPhysicBody), "_Index");
                    if (bodyIndexField != null)
                    {
                        for (int i = 0; i < bodies.Count; i++)
                        {
                            if (bodies[i] != null)
                                bodyIndexField.SetValue(bodies[i], i);
                        }
                    }
                    ModLogger.Info($"[GameBridge] Re-ordered {bodies.Count} physics bodies deterministically (preserved terrain/gate colliders).");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] SortEntitiesById failed: {ex.InnerException ?? ex}");
            }
        }

        // ====================================================================
        // Deterministic clock / RNG / fog / camera helpers (P0 & P1 fixes).
        // ====================================================================

        /// <summary>
        /// Re-seats the 30Hz logic clock and the 12Hz physics clock on the current GameTime so both
        /// peers start a run with an identical zero-phase boundary. NEVER set these to 0.0: on a
        /// late-game save (GameTime ≈ 5.6e6 ms) that makes UpdatePhysics run tens of thousands of
        /// steps in one frame and desyncs the two processes.
        /// </summary>
        public static void AlignSimulationClocks()
        {
            var game = DXGame.Current;
            if (game == null) return;
            try
            {
                double gt = game.GameTime;
                DXGame.LastGameTimeIAUpdate = gt;
                DXPhysicWorld.LastGameTimeUpdatePhysics = gt;
                DXPhysicWorld.TimeBodyPosition = gt;
                DXPhysicWorld.TimeLastBodyPosition = gt;
                ModLogger.Info($"[GameBridge] Simulation clocks aligned at GameTime={gt:F1}ms.");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] AlignSimulationClocks failed: {ex.Message}");
            }
        }

        /// <summary>Re-seeds DXLevel.Current.Random and ZXGameState.Current.Random so Host and Client roll identical mayors, events, and inn mercenaries.</summary>
        public static void SeedLevelRandom(int seed)
        {
            try
            {
                var level = DXLevel.Current;
                if (level != null)
                    level.Random = new DXRandom(seed);

                // TheInn.OnUpdate() rolls mercenary pools from ZXGameState.Current.Random rather than
                // DXLevel.Current.Random; sync it identically across peers.
                object gs = CurrentGameStateObject;
                if (gs != null)
                {
                    if (_propGameStateRandom == null)
                        _propGameStateRandom = _typeZXGameState?.GetProperty("Random", BindingFlags.Public | BindingFlags.Instance);
                    if (_propGameStateRandom != null && _propGameStateRandom.CanWrite)
                        _propGameStateRandom.SetValue(gs, new DXRandom(unchecked(seed ^ 0x5A475352)), null);
                }

                ModLogger.Info($"[GameBridge] Re-seeded DXLevel.Current.Random (seed={seed}) and ZXGameState.Random.");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] SeedLevelRandom failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Makes every entity visible immediately. Beyond flipping ShowFullMap/FogEnabled, this clears
        /// the three ZXSystemMapLayers watch arrays and forces SceneObject.Opacity=1/Visible=true:
        /// CInvisibleWhenNotWatched uses the Point overload that ignores FogEnabled and only fades in
        /// by +0.09 every 4 logic ticks, so without this reveal_map shows an empty map for ~45 ticks.
        /// </summary>
        public static bool RevealAll(bool reveal)
        {
            bool ok = SetShowFullMap(reveal);
            SetFogEnabled(!reveal);

            try
            {
                if (!reveal) return ok;

                object inst = GetMapLayersInstance();
                if (inst != null)
                {
                    ClearIntArray(inst, ObfuscatedSymbols.Field_MapLayers_VisibleCells);
                    ClearIntArray(inst, ObfuscatedSymbols.Field_MapLayers_VisibleBackBuf);
                    ClearIntArray(inst, ObfuscatedSymbols.Field_MapLayers_ExploredCells);
                    SetInstanceBool(inst, "LayerFogUpdated", true);
                    SetInstanceBool(inst, "LayerShadowUpdated", true);
                }

                var game = DXGame.Current;
                if (game?.Entities != null)
                {
                    foreach (var e in game.Entities)
                    {
                        var so = e?.SceneObject;
                        if (so == null) continue;
                        so.Opacity = 1f;
                        so.Visible = true;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] RevealAll failed: {ex.Message}");
            }
            return ok;
        }

        public static object GetMapLayersInstance()
        {
            if (_mapLayersType == null)
                _mapLayersType = GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXSystemMapLayers);
            if (_mapLayersType == null) return null;
            if (_mapLayersCurrentField == null)
                _mapLayersCurrentField = _mapLayersType.GetField(ObfuscatedSymbols.Field_MapLayers_Current,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return _mapLayersCurrentField?.GetValue(null);
        }

        private static void ClearIntArray(object instance, string fieldName)
        {
            var f = _mapLayersType?.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(instance) is int[] arr) Array.Clear(arr, 0, arr.Length);
        }

        private static void SetInstanceBool(object instance, string propName, bool value)
        {
            var p = _mapLayersType?.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) p.SetValue(instance, value);
        }

        /// <summary>
        /// Centers the camera on a WORLD point. entity.Position is world-grid; CameraArea is isometric
        /// scene space, so the point must go through DXWorldGrid.ScenePointFromWorldPoint. Also clears
        /// the opening zoom animation flag which otherwise overwrites CameraArea every frame.
        /// </summary>
        public static bool SetCameraWorld(PointF worldCenter, float width = 0f, float height = 0f)
        {
            var scene = DXScene.Current;
            if (scene == null) return false;

            DisableCameraIntroAnimation();

            RectangleF cur = scene.CameraArea;
            Rectangle screen = DXScene.ScreenArea;
            float aspect = (screen.Width > 0 && screen.Height > 0)
                ? (float)screen.Width / screen.Height
                : ((cur.Width > 0f && cur.Height > 0f) ? cur.Width / cur.Height : 0f);
            if (aspect <= 0f) return false;

            float w = width;
            if (w <= 0f && height > 0f) w = height * aspect;
            if (w <= 0f) w = cur.Width;
            if (w <= 0f) return false;

            float maxW = Math.Max(15f, 45f * aspect);
            w = Math.Max(15f, Math.Min(w, maxW));
            float h = w / aspect;

            var scenePt = DXWorldGrid.ScenePointFromWorldPoint(worldCenter);
            scene.CameraArea = new RectangleF(scenePt.X - w * 0.5f, scenePt.Y - h * 0.5f, w, h);
            ModLogger.Info($"[GameBridge] Camera world=({worldCenter.X:F1},{worldCenter.Y:F1}) -> scene=({scenePt.X:F1},{scenePt.Y:F1}) size={w:F1}x{h:F1} aspect={aspect:F3}");
            return true;
        }

        private static FieldInfo _fLevelSysHudRoot;
        private static FieldInfo _fLevelSysIntroCinematic;
        private static FieldInfo _fLevelSysScreenAnim;

        private static void EnsureCameraIntroFields()
        {
            if (TypeZXSystemGameLevel == null) return;
            if (_fLevelSysHudRoot == null)
                _fLevelSysHudRoot = AccessTools.Field(TypeZXSystemGameLevel, "#=zNjT9FdpMLDa2");
            if (_fLevelSysIntroCinematic == null)
                _fLevelSysIntroCinematic = AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_CameraIntroAnim);
            if (_fLevelSysScreenAnim == null)
                _fLevelSysScreenAnim = AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_ScreenAnim);
        }

        public static void EnforceDisabledCameraIntroForLevel(object levelSys)
        {
            try
            {
                EnsureCameraIntroFields();
                if (_fLevelSysIntroCinematic != null && _fLevelSysIntroCinematic.GetValue(null) is bool intro && intro)
                {
                    _fLevelSysIntroCinematic.SetValue(null, false);
                    _fLevelSysScreenAnim?.SetValue(null, null);
                    var rootObj = DXScene.Current?.RootObject;
                    if (rootObj != null)
                    {
                        rootObj.Blur = 0f;
                        if (rootObj.State != null) rootObj.State.Blur = 0f;
                    }
                }

                if (levelSys != null && _fLevelSysHudRoot != null && _fLevelSysHudRoot.GetValue(levelSys) is DXObject hudObj)
                {
                    bool clearedAnim = false;
                    if (hudObj.Animation != null && hudObj.Animation.Animating)
                    {
                        hudObj.Animation.Clear();
                        clearedAnim = true;
                    }
                    var sp = DXScreenParameters.Current;
                    if (sp != null && !sp.ViewArea.IsEmpty && (clearedAnim || Math.Abs(hudObj.Y - sp.ViewArea.Y) > 0.5f))
                    {
                        hudObj.Area = sp.ViewArea;
                        hudObj.Opacity = 1f;
                    }
                }
            }
            catch { }
        }

        public static void DisableCameraIntroAnimation()
        {
            try
            {
                EnsureCameraIntroFields();
                _fLevelSysIntroCinematic?.SetValue(null, false);
                _fLevelSysScreenAnim?.SetValue(null, null);

                var rootObj = DXScene.Current?.RootObject;
                if (rootObj != null)
                {
                    rootObj.Blur = 0f;
                    if (rootObj.State != null) rootObj.State.Blur = 0f;
                }

                var levelSys = LevelSystemInstance;
                if (levelSys != null && _fLevelSysHudRoot != null && _fLevelSysHudRoot.GetValue(levelSys) is DXObject hudObj)
                {
                    if (hudObj.Animation != null && hudObj.Animation.Animating)
                        hudObj.Animation.Clear();
                    var sp = DXScreenParameters.Current;
                    if (sp != null && !sp.ViewArea.IsEmpty && Math.Abs(hudObj.Y - sp.ViewArea.Y) > 0.5f)
                    {
                        hudObj.Area = sp.ViewArea;
                    }
                    hudObj.Opacity = 1f;
                }
            }
            catch { }
        }

        public static string GetHudDiagSummary()
        {
            try
            {
                var levelSys = LevelSystemInstance;
                var fHud = TypeZXSystemGameLevel != null ? AccessTools.Field(TypeZXSystemGameLevel, "#=zNjT9FdpMLDa2") : null;
                var fIntro = TypeZXSystemGameLevel != null ? AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_CameraIntroAnim) : null;
                var fIntroTime = TypeZXSystemGameLevel != null ? AccessTools.Field(TypeZXSystemGameLevel, "#=zIOUagVzqxvgnQeV3rA==") : null;
                var fIntroBlur = TypeZXSystemGameLevel != null ? AccessTools.Field(TypeZXSystemGameLevel, "#=z40N_lqqB8daJ") : null;
                var fIsLoadSave = TypeZXGame != null ? AccessTools.Field(TypeZXGame, "#=zAVyuAmKGGVLXLOjjSQ==") : null;

                var hudObj = levelSys != null && fHud != null ? fHud.GetValue(levelSys) as DXObject : null;
                bool introFlag = fIntro?.GetValue(null) is bool b && b;
                int introStart = fIntroTime?.GetValue(null) is int t ? t : -1;
                var introBlur = fIntroBlur?.GetValue(null) as DXAnimation;
                var gx = _mZXGameCurrent?.Invoke(null, null);
                bool isLoadSave = gx != null && fIsLoadSave?.GetValue(gx) is bool ls && ls;

                var scene = DXScene.Current;
                var sp = DXScreenParameters.Current;
                var viewArea = sp?.ViewArea ?? Rectangle.Empty;
                float? fixedAr = sp?.FixedAspectRatio;
                var cam = scene?.CameraArea ?? RectangleF.Empty;
                var screen = scene != null ? DXScene.ScreenArea : Rectangle.Empty;
                float unit = DXScene.Unit;
                float rootBlur = scene?.RootObject?.Blur ?? -1f;
                float scaleX = scene?.RootObject?.ScalingX ?? -1f;
                float scaleY = scene?.RootObject?.ScalingY ?? -1f;
                int vtime = scene?.VirtualTimeInt ?? -1;

                string clipDesc = "";
                if (hudObj?.Animation != null && hudObj.Animation.Clips != null && hudObj.Animation.Clips.Count > 0)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var cl in hudObj.Animation.Clips)
                    {
                        foreach (var tr in cl.Transitions)
                        {
                            parts.Add($"[dur={tr.Duration},loc={tr.RangeLocation},op={tr.RangeOpacity}]");
                        }
                    }
                    clipDesc = ",clips=" + string.Join(";", parts);
                }

                string hudStr = hudObj != null
                    ? $"({hudObj.X:F1},{hudObj.Y:F1},{hudObj.Width:F1}x{hudObj.Height:F1},anim={hudObj.Animation?.Animating}{clipDesc},vis={hudObj.Visible},style={hudObj.State?.AreaStyle})"
                    : "null";

                string fixedArStr = fixedAr.HasValue ? fixedAr.Value.ToString("F3") : "null";
                return $"hud={hudStr} intro={introFlag}(t0={introStart},vt={vtime}) blurAnim={(introBlur != null ? (introBlur.Finished ? "done" : "running") : "null")} rootBlur={rootBlur:F3} isLoadSave={isLoadSave} unit={unit:F3} view={viewArea} screen={screen} cam=({cam.X:F1},{cam.Y:F1},{cam.Width:F1}x{cam.Height:F1}) scale=({scaleX:F2},{scaleY:F2}) fixedAR={fixedArStr}";
            }
            catch (Exception ex)
            {
                return $"err:{ex.Message}";
            }
        }

        /// <summary>
        /// Returns the entity Type produced by a Build / EntityBonusCreation command (both internal,
        /// so the caller passes them as object). Used to capture And replay building placements.
        /// </summary>
        public static Type GetBuildingTypeFromCommand(object command)
        {
            if (command == null) return null;
            Type t = command.GetType();
            var entityTypeProp = t.GetProperty("EntityType", BindingFlags.Public | BindingFlags.Instance);
            if (entityTypeProp != null)
            {
                try { if (entityTypeProp.GetValue(command) is Type et) return et; } catch { }
            }
            var entityProp = t.GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
            if (entityProp != null)
            {
                try { return (entityProp.GetValue(command) as DXEntity)?.GetType(); } catch { }
            }
            return null;
        }


        public static List<DXEntity> GetPlayerEntities()
        {
            var result = new List<DXEntity>();
            var game = DXGame.Current;
            if (game?.Entities == null) return result;

            foreach (var entity in game.Entities)
            {
                if (entity == null || entity.Disposed) continue;
                if ((entity.ID & 0x8000000000000000UL) != 0) continue;
                if (IsPlayerTeam(entity)) result.Add(entity);
            }
            result.Sort((a, b) => a.ID.CompareTo(b.ID));
            return result;
        }

        public static List<DXEntity> GetPlayerEntitiesByTypeName(string typeNameSubstring, int maxCount = int.MaxValue)
        {
            var result = new List<DXEntity>();
            if (string.IsNullOrEmpty(typeNameSubstring)) return result;

            foreach (var entity in GetPlayerEntities())
            {
                if (entity.GetType().Name.IndexOf(typeNameSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Add(entity);
                    if (result.Count >= maxCount) break;
                }
            }
            return result;
        }

        private static Func<object, int> _fastGetEntityTeamInt;
        private static int _playerTeamIntValue = int.MinValue;
        private static Func<object, bool> _fastIsEntityEnabled;
        private static Func<object, int> _fastGetEnergyTransferRadius;
        private static Func<object, uint> _fastGetPathfindingMask;
        private static Func<object, int> _fastGetMovableCellSize;
        private static Func<object, PointF> _fastGetCmdTargetPos;
        private static bool _perfDelegatesCompiled;

        private static void TryCompileFastPerfDelegates()
        {
            if (_perfDelegatesCompiled) return;
            _perfDelegatesCompiled = true;
            try
            {
                if (TypeZXEntity != null)
                {
                    if (_propEntityTeam == null)
                        _propEntityTeam = TypeZXEntity.GetProperty("Team", BindingFlags.Public | BindingFlags.Instance);
                    var teamGetter = _propEntityTeam?.GetGetMethod(true);
                    if (teamGetter != null && _propEntityTeam.PropertyType.IsEnum)
                    {
                        _playerTeamIntValue = Convert.ToInt32(Enum.Parse(_propEntityTeam.PropertyType, "Player", true));
                        var dmTeam = new DynamicMethod(
                            "TABCoop_FastGetEntityTeamInt",
                            typeof(int),
                            new[] { typeof(object) },
                            typeof(GameBridge).Module,
                            skipVisibility: true);
                        var ilT = dmTeam.GetILGenerator();
                        var lblBad = ilT.DefineLabel();
                        ilT.Emit(OpCodes.Ldarg_0);
                        ilT.Emit(OpCodes.Isinst, TypeZXEntity);
                        ilT.Emit(OpCodes.Dup);
                        ilT.Emit(OpCodes.Brfalse_S, lblBad);
                        ilT.Emit(OpCodes.Callvirt, teamGetter);
                        ilT.Emit(OpCodes.Conv_I4);
                        ilT.Emit(OpCodes.Ret);
                        ilT.MarkLabel(lblBad);
                        ilT.Emit(OpCodes.Pop);
                        ilT.Emit(OpCodes.Ldc_I4_M1);
                        ilT.Emit(OpCodes.Ret);
                        _fastGetEntityTeamInt = (Func<object, int>)dmTeam.CreateDelegate(typeof(Func<object, int>));
                    }

                    if (_pStructEnabled == null)
                        _pStructEnabled = TypeZXEntity.GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
                    var enabledGetter = _pStructEnabled?.GetGetMethod(true);
                    if (enabledGetter != null)
                    {
                        var dmEn = new DynamicMethod(
                            "TABCoop_FastIsEntityEnabled",
                            typeof(bool),
                            new[] { typeof(object) },
                            typeof(GameBridge).Module,
                            skipVisibility: true);
                        var ilE = dmEn.GetILGenerator();
                        var lblBad = ilE.DefineLabel();
                        ilE.Emit(OpCodes.Ldarg_0);
                        ilE.Emit(OpCodes.Isinst, TypeZXEntity);
                        ilE.Emit(OpCodes.Dup);
                        ilE.Emit(OpCodes.Brfalse_S, lblBad);
                        ilE.Emit(OpCodes.Callvirt, enabledGetter);
                        ilE.Emit(OpCodes.Ret);
                        ilE.MarkLabel(lblBad);
                        ilE.Emit(OpCodes.Pop);
                        ilE.Emit(OpCodes.Ldc_I4_0);
                        ilE.Emit(OpCodes.Ret);
                        _fastIsEntityEnabled = (Func<object, bool>)dmEn.CreateDelegate(typeof(Func<object, bool>));
                    }

                    var radiusGetter = _propDefaultParamsEnergyRadius?.GetGetMethod(true);
                    if (_methodEntityGetDefaultParams != null && radiusGetter != null)
                    {
                        var dmRad = new DynamicMethod(
                            "TABCoop_FastGetEnergyTransferRadius",
                            typeof(int),
                            new[] { typeof(object) },
                            typeof(GameBridge).Module,
                            skipVisibility: true);
                        var ilR = dmRad.GetILGenerator();
                        var lblZero = ilR.DefineLabel();
                        ilR.Emit(OpCodes.Ldarg_0);
                        ilR.Emit(OpCodes.Isinst, TypeZXEntity);
                        ilR.Emit(OpCodes.Dup);
                        ilR.Emit(OpCodes.Brfalse_S, lblZero);
                        ilR.Emit(OpCodes.Callvirt, _methodEntityGetDefaultParams);
                        ilR.Emit(OpCodes.Dup);
                        ilR.Emit(OpCodes.Brfalse_S, lblZero);
                        ilR.Emit(OpCodes.Callvirt, radiusGetter);
                        ilR.Emit(OpCodes.Conv_I4);
                        ilR.Emit(OpCodes.Ret);
                        ilR.MarkLabel(lblZero);
                        ilR.Emit(OpCodes.Pop);
                        ilR.Emit(OpCodes.Ldc_I4_0);
                        ilR.Emit(OpCodes.Ret);
                        _fastGetEnergyTransferRadius = (Func<object, int>)dmRad.CreateDelegate(typeof(Func<object, int>));
                    }

                    var maskGetter = TypeZXEntity.GetProperty("PathfindingMask", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod(true);
                    if (maskGetter != null)
                    {
                        var dmMask = new DynamicMethod(
                            "TABCoop_FastGetPathfindingMask",
                            typeof(uint),
                            new[] { typeof(object) },
                            typeof(GameBridge).Module,
                            skipVisibility: true);
                        var ilM = dmMask.GetILGenerator();
                        var lblAll = ilM.DefineLabel();
                        ilM.Emit(OpCodes.Ldarg_0);
                        ilM.Emit(OpCodes.Isinst, TypeZXEntity);
                        ilM.Emit(OpCodes.Dup);
                        ilM.Emit(OpCodes.Brfalse_S, lblAll);
                        ilM.Emit(OpCodes.Callvirt, maskGetter);
                        ilM.Emit(OpCodes.Conv_U4);
                        ilM.Emit(OpCodes.Ret);
                        ilM.MarkLabel(lblAll);
                        ilM.Emit(OpCodes.Pop);
                        ilM.Emit(OpCodes.Ldc_I4_M1);
                        ilM.Emit(OpCodes.Conv_U4);
                        ilM.Emit(OpCodes.Ret);
                        _fastGetPathfindingMask = (Func<object, uint>)dmMask.CreateDelegate(typeof(Func<object, uint>));
                    }
                }

                if (TypeCMovable != null)
                {
                    var fCellSize = TypeCMovable.GetField("#=zaEz384L8BdkBD$9eFb9jRg5sK8kR", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (fCellSize != null && fCellSize.FieldType == typeof(int))
                    {
                        var dmCs = new DynamicMethod(
                            "TABCoop_FastGetMovableCellSize",
                            typeof(int),
                            new[] { typeof(object) },
                            typeof(GameBridge).Module,
                            skipVisibility: true);
                        var ilC = dmCs.GetILGenerator();
                        var lblOne = ilC.DefineLabel();
                        ilC.Emit(OpCodes.Ldarg_0);
                        ilC.Emit(OpCodes.Isinst, TypeCMovable);
                        ilC.Emit(OpCodes.Dup);
                        ilC.Emit(OpCodes.Brfalse_S, lblOne);
                        ilC.Emit(OpCodes.Ldfld, fCellSize);
                        ilC.Emit(OpCodes.Ret);
                        ilC.MarkLabel(lblOne);
                        ilC.Emit(OpCodes.Pop);
                        ilC.Emit(OpCodes.Ldc_I4_1);
                        ilC.Emit(OpCodes.Ret);
                        _fastGetMovableCellSize = (Func<object, int>)dmCs.CreateDelegate(typeof(Func<object, int>));
                    }
                }

                EnsureCommandTargetMeta();
                var serPosGetter = _pSerPosition?.GetGetMethod(true);
                if (_typeZXCommandTargetRef != null && serPosGetter != null && serPosGetter.ReturnType == typeof(PointF))
                {
                    var dmPos = new DynamicMethod(
                        "TABCoop_FastGetCmdTargetPos",
                        typeof(PointF),
                        new[] { typeof(object) },
                        typeof(GameBridge).Module,
                        skipVisibility: true);
                    var ilP = dmPos.GetILGenerator();
                    var lblEmpty = ilP.DefineLabel();
                    var locEmpty = ilP.DeclareLocal(typeof(PointF));
                    ilP.Emit(OpCodes.Ldarg_0);
                    ilP.Emit(OpCodes.Isinst, _typeZXCommandTargetRef);
                    ilP.Emit(OpCodes.Brfalse_S, lblEmpty);
                    ilP.Emit(OpCodes.Ldarg_0);
                    if (_typeZXCommandTargetRef.IsValueType)
                    {
                        ilP.Emit(OpCodes.Unbox, _typeZXCommandTargetRef);
                        ilP.Emit(OpCodes.Call, serPosGetter);
                    }
                    else
                    {
                        ilP.Emit(OpCodes.Castclass, _typeZXCommandTargetRef);
                        ilP.Emit(OpCodes.Callvirt, serPosGetter);
                    }
                    ilP.Emit(OpCodes.Ret);
                    ilP.MarkLabel(lblEmpty);
                    ilP.Emit(OpCodes.Ldloca_S, locEmpty);
                    ilP.Emit(OpCodes.Initobj, typeof(PointF));
                    ilP.Emit(OpCodes.Ldloc, locEmpty);
                    ilP.Emit(OpCodes.Ret);
                    _fastGetCmdTargetPos = (Func<object, PointF>)dmPos.CreateDelegate(typeof(Func<object, PointF>));
                }

                EnsureFastEconomyPerfDelegates();
            }
            catch (Exception ex)
            {
                _fastGetEntityTeamInt = null;
                _fastIsEntityEnabled = null;
                _fastGetEnergyTransferRadius = null;
                ModLogger.Warn($"[Perf] TryCompileFastPerfDelegates failed (falling back to reflection): {ex.Message}");
            }
        }

        public static bool IsPlayerEntity(object entity) => IsPlayerTeam(entity);

        private static bool IsPlayerTeam(object entity)
        {
            if (entity == null) return false;
            var fastTeam = _fastGetEntityTeamInt;
            if (fastTeam != null)
                return fastTeam(entity) == _playerTeamIntValue;

            Type t = entity.GetType();
            if (!_teamPropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("Team", BindingFlags.Public | BindingFlags.Instance);
                _teamPropCache[t] = prop;
            }
            if (prop == null) return false;
            try { return string.Equals(prop.GetValue(entity)?.ToString(), "Player", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static DXEntity GetCommandCenterEntity()
        {
            var ls = CurrentLevelState;
            if (ls == null) return null;
            if (_ccProp == null)
                _ccProp = LevelStateType?.GetProperty("CurrentCommandCenter", BindingFlags.Public | BindingFlags.Instance);
            if (_ccProp == null) return null;
            try { return _ccProp.GetValue(ls) as DXEntity; } catch { return null; }
        }

        public static object GetLifeObject(object entity)
        {
            if (entity == null || _mGetEntityLife == null) return null;
            if (!_mGetEntityLife.DeclaringType.IsInstanceOfType(entity)) return null;
            if (_fastGetLifeObj != null)
            {
                try { return _fastGetLifeObj(entity); } catch { return null; }
            }
            try { return _mGetEntityLife.Invoke(entity, null); } catch { return null; }
        }

        public static int GetLifeValue(object entity)
        {
            object life = GetLifeObject(entity);
            if (life == null) return 0;
            if (_fastGetLifeInt != null && _fastGetLifeIntDeclType != null && _fastGetLifeIntDeclType.IsInstanceOfType(life))
            {
                try { return _fastGetLifeInt(life); } catch { return 0; }
            }
            Type t = life.GetType();
            if (!_lifePropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("Life", BindingFlags.Public | BindingFlags.Instance);
                _lifePropCache[t] = prop;
            }
            if (prop == null) return 0;
            try { return Convert.ToInt32(prop.GetValue(life)); } catch { return 0; }
        }

        private static readonly Dictionary<Type, PropertyInfo> _isAlivePropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _deathProcessedPropCache = new Dictionary<Type, PropertyInfo>();

        /// <summary>
        /// Host-authoritative HP correction for async alignment. Applies lifeDelta to the current
        /// CLife.Life (or forces 0 when forceKill) only while the entity is still alive locally; the
        /// engine's own CLife update then runs normal death processing on the next tick.
        /// </summary>
        public static bool TryCorrectLife(object entity, int lifeDelta, bool forceKill)
        {
            object life = GetLifeObject(entity);
            if (life == null) return false;
            Type t = life.GetType();
            try
            {
                if (!_lifePropCache.TryGetValue(t, out PropertyInfo lifeProp))
                {
                    lifeProp = t.GetProperty("Life", BindingFlags.Public | BindingFlags.Instance);
                    _lifePropCache[t] = lifeProp;
                }
                if (lifeProp == null || !lifeProp.CanWrite) return false;

                if (!_isAlivePropCache.TryGetValue(t, out PropertyInfo aliveProp))
                {
                    aliveProp = t.GetProperty("IsAlive", BindingFlags.Public | BindingFlags.Instance);
                    _isAlivePropCache[t] = aliveProp;
                }
                if (!_deathProcessedPropCache.TryGetValue(t, out PropertyInfo deadProp))
                {
                    deadProp = t.GetProperty("DeathProccessed", BindingFlags.Public | BindingFlags.Instance);
                    _deathProcessedPropCache[t] = deadProp;
                }
                if (aliveProp != null && !(bool)aliveProp.GetValue(life)) return false;
                if (deadProp != null && (bool)deadProp.GetValue(life)) return false;

                int cur = Convert.ToInt32(lifeProp.GetValue(life));
                int next = forceKill ? 0 : cur + lifeDelta;
                if (next < 0) next = 0;
                if (next == cur) return false;
                lifeProp.SetValue(life, next);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Computes a deterministic aggro/alert signature for a Zombie entity without touching internal
        /// types at compile time. Returns 0 when idle/roaming so pure micro-physics nudges never trip aggro alerts.
        /// </summary>
        public static bool GetUnitAttackCommandCenter(DXEntity entity)
        {
            if (entity == null || _typeZombie == null || !_typeZombie.IsInstanceOfType(entity))
                return false;
            if (!_zombieAggroReflectionInit)
            {
                _zombieAggroReflectionInit = true;
                _propZombieAttackCC = _typeZombie.GetProperty("AttackCommandCenter", BindingFlags.Public | BindingFlags.Instance);
            }
            if (_propZombieAttackCC == null) return false;
            try { return (bool)_propZombieAttackCC.GetValue(entity); }
            catch { return false; }
        }

        public static long GetZombieAggroSignature(DXEntity entity, out bool isAlerted)
        {
            isAlerted = false;
            if (entity == null || _typeZombie == null || !_typeZombie.IsInstanceOfType(entity))
                return 0L;

            try
            {
                if (!_zombieAggroReflectionInit)
                {
                    _zombieAggroReflectionInit = true;
                    _propZombieAttackCC = _typeZombie.GetProperty("AttackCommandCenter", BindingFlags.Public | BindingFlags.Instance);
                }

                bool attackCC = _propZombieAttackCC != null && (bool)_propZombieAttackCC.GetValue(entity);

                int moveMode = 0;
                if (_mGetEntityMovable != null)
                {
                    object movable = _mGetEntityMovable.Invoke(entity, null);
                    if (movable != null)
                    {
                        if (_propMovableMode == null)
                            _propMovableMode = movable.GetType().GetProperty("MovementMode", BindingFlags.Public | BindingFlags.Instance);
                        if (_propMovableMode != null)
                            moveMode = Convert.ToInt32(_propMovableMode.GetValue(movable));
                    }
                }

                ulong targetId = 0;
                bool hasLastEnemy = false;
                if (_mGetEntityBehaviour != null)
                {
                    object cbehav = _mGetEntityBehaviour.Invoke(entity, null);
                    if (cbehav != null)
                    {
                        if (_propCBehavBehaviour == null)
                            _propCBehavBehaviour = cbehav.GetType().GetProperty("Behaviour", BindingFlags.Public | BindingFlags.Instance);
                        object behav = _propCBehavBehaviour?.GetValue(cbehav);
                        if (behav != null)
                        {
                            if (_propBehavData == null)
                                _propBehavData = behav.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                            object bdata = _propBehavData?.GetValue(behav);
                            if (bdata != null)
                            {
                                Type bType = bdata.GetType();
                                if (_fieldBDataTargetRef == null)
                                    _fieldBDataTargetRef = bType.GetField(ObfuscatedSymbols.Field_BData_TargetRef, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (_fieldBDataTargetRef != null)
                                {
                                    object boxedRef = _fieldBDataTargetRef.GetValue(bdata);
                                    if (boxedRef != null)
                                    {
                                        if (_propTargetRefId == null)
                                            _propTargetRefId = boxedRef.GetType().GetProperty("IDEntity", BindingFlags.Public | BindingFlags.Instance);
                                        if (_propTargetRefId != null)
                                            targetId = Convert.ToUInt64(_propTargetRefId.GetValue(boxedRef));
                                    }
                                }

                                if (_propBDataLastEnemy == null)
                                    _propBDataLastEnemy = bType.GetProperty("LastEnemyPosition", BindingFlags.Public | BindingFlags.Instance);
                                if (_propBDataLastEnemy != null)
                                {
                                    object lastEnemyObj = _propBDataLastEnemy.GetValue(bdata);
                                    hasLastEnemy = lastEnemyObj != null;
                                }
                            }
                        }
                    }
                }

                // TMovementMode: Walk=0, WalkFast=1, Run=2, Jump=3
                bool chasingMode = moveMode >= 2;
                if (targetId == 0 && !hasLastEnemy && !chasingMode && !attackCC)
                    return 0L;

                isAlerted = true;
                long sig = (long)entity.ID
                    ^ ((long)targetId * 73856093L)
                    ^ ((long)moveMode * 19349663L)
                    ^ (hasLastEnemy ? 83492791L : 0L)
                    ^ (attackCC ? 2654435761L : 0L);
                return sig != 0L ? sig : 1L;
            }
            catch
            {
                return 0L;
            }
        }

        /// <summary>
        /// Returns true if the entity has significant physics velocity or an active CMovable
        /// movement target, so mid-march player units are not nudged unnecessarily on sub-threshold drift.
        /// </summary>
        public static bool IsEntityActivelyMoving(DXEntity entity)
        {
            if (entity == null || entity.Disposed) return false;
            try
            {
                var body = entity.Body;
                if (body != null && body.Handle != IntPtr.Zero)
                {
                    var v = body.Velocity;
                    if (v.X * v.X + v.Y * v.Y > 0.04f)
                        return true;
                }

                if (_mGetEntityMovable != null && _mGetEntityMovable.DeclaringType.IsInstanceOfType(entity))
                {
                    object movable = _mGetEntityMovable.Invoke(entity, null);
                    if (movable != null)
                    {
                        if (_fieldMovableTargetPos == null)
                            _fieldMovableTargetPos = movable.GetType().GetField("#=zx1L3mWRqDcT7", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_fieldMovableTargetPos != null && _fieldMovableTargetPos.GetValue(movable) != null)
                            return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// Returns the minimum positional drift (in cells) required before async correction snaps an
        /// entity, along with the damping factor applied to sub-2.2-cell nudges.
        /// </summary>
        public static float GetEntityPositionSnapThreshold(DXEntity entity, out float dampingFactor)
        {
            if (entity == null)
            {
                dampingFactor = 0.65f;
                return 1.0f;
            }

            if (IsPlayerEntity(entity))
            {
                if (IsEntityActivelyMoving(entity))
                {
                    dampingFactor = 0.60f;
                    return 1.80f;
                }
                dampingFactor = 0.75f;
                return 0.50f;
            }

            if (IsZombieType(entity.GetType()))
            {
                GetZombieAggroSignature(entity, out bool isAlerted);
                if (isAlerted)
                {
                    dampingFactor = 0.70f;
                    return IsEntityActivelyMoving(entity) ? 0.85f : 0.55f;
                }
                dampingFactor = 0.60f;
                return 1.25f;
            }

            dampingFactor = 0.65f;
            return 1.00f;
        }

        public static object GetCommandableObject(object entity)
        {
            if (entity == null || _mGetEntityCmd == null) return null;
            if (!_mGetEntityCmd.DeclaringType.IsInstanceOfType(entity)) return null;
            if (_fastGetCmdable != null)
            {
                try { return _fastGetCmdable(entity); } catch { return null; }
            }
            try { return _mGetEntityCmd.Invoke(entity, null); } catch { return null; }
        }

        public static IList GetOrdersQueued(object cmdComp)
        {
            if (cmdComp == null) return null;
            if (_fastGetOrdersQueued != null && _fastGetOrdersQueuedDeclType != null && _fastGetOrdersQueuedDeclType.IsInstanceOfType(cmdComp))
            {
                try { return _fastGetOrdersQueued(cmdComp); } catch { return null; }
            }
            Type t = cmdComp.GetType();
            if (!_ordersPropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("OrdersQueued", BindingFlags.Public | BindingFlags.Instance);
                _ordersPropCache[t] = prop;
            }
            if (prop == null) return null;
            try { return prop.GetValue(cmdComp) as IList; } catch { return null; }
        }

        public static int GetOrderCommandId(object order)
        {
            if (order == null) return 0;
            Type t = order.GetType();
            if (!_idCommandCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("IDCommand", BindingFlags.Public | BindingFlags.Instance);
                _idCommandCache[t] = prop;
            }
            if (prop == null) return 0;
            try { return Convert.ToInt32(prop.GetValue(order)); } catch { return 0; }
        }

        private static readonly Dictionary<Type, PropertyInfo> _idCommandCache = new Dictionary<Type, PropertyInfo>();

        public static object GetCommandGeneric(Type commandType)
        {
            var generic = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_GetGeneric);
            if (generic == null || !generic.IsGenericMethodDefinition) return null;
            return generic.MakeGenericMethod(commandType).Invoke(null, null);
        }

        // ====================================================================
        // Command execution internals (all object + reflection; ZXCommand,
        // ZXCommandTarget, Build, Train, CCommandable and CMovable are internal).
        // ====================================================================

        private static readonly Dictionary<Type, PropertyInfo> _cmdIdPropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _cmdableTargetPropCache = new Dictionary<Type, PropertyInfo>();
        private static MethodInfo _mBuildGetForType;
        private static MethodInfo _mTrainGetForType;

        private static Type _typeZXCommand;
        private static Type _typeCMovable;
        private static Type _typeCCommandable;
        private static Type[] _groupRoutedCommandTypes;
        private static readonly Dictionary<Type, PropertyInfo> _cmdIndicatorColorCache = new Dictionary<Type, PropertyInfo>();

        public static Type TypeZXCommand
        {
            get
            {
                if (_typeZXCommand == null) _typeZXCommand = GameAssembly?.GetType("ZX.Commands.ZXCommand");
                return _typeZXCommand;
            }
        }

        public static Type TypeZXEntity => _mCmdExec?.GetParameters()[0].ParameterType;
        public static Type TypeZXCommandTarget => _mCmdExec?.GetParameters()[1].ParameterType;

        public static Type TypeCMovable
        {
            get
            {
                if (_typeCMovable == null) _typeCMovable = GameAssembly?.GetType("ZX.Components.CMovable");
                return _typeCMovable;
            }
        }

        public static Type TypeCCommandable
        {
            get
            {
                if (_typeCCommandable == null) _typeCCommandable = _mGetEntityCmd?.ReturnType ?? GameAssembly?.GetType("ZX.Components.CCommandable");
                return _typeCCommandable;
            }
        }

        public static int GetCommandId(object command)
        {
            if (command == null) return 0;
            Type t = command.GetType();
            if (!_cmdIdPropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("ID", BindingFlags.Public | BindingFlags.Instance);
                _cmdIdPropCache[t] = prop;
            }
            if (prop == null) return 0;
            try { return Convert.ToInt32(prop.GetValue(command)); } catch { return 0; }
        }

        public static ulong[] GetSelectedPlayerEntityIds()
        {
            var ids = new List<ulong>();
            foreach (object item in EnumerateSelectedComponents())
            {
                var comp = item as DXComponent;
                var ent = comp?.Entity;
                if (ent == null || ent.Disposed) continue;
                var filter = LocalEntityControlFilter;
                if (HasComponent(ent, TypeCMovable) && HasComponent(ent, TypeCCommandable) && IsPlayerEntity(ent)
                    && (filter == null || filter(ent)))
                    ids.Add(ent.ID);
            }
            ids.Sort();
            return ids.ToArray();
        }

        public static bool IsGroupRoutedCommand(object command)
        {
            if (command == null) return false;
            if (_groupRoutedCommandTypes == null && GameAssembly != null)
            {
                _groupRoutedCommandTypes = new[]
                {
                    GameAssembly.GetType("ZX.Commands.Travel"),
                    GameAssembly.GetType("ZX.Commands.Patrol"),
                    GameAssembly.GetType("ZX.Commands.Chase"),
                    GameAssembly.GetType("ZX.Commands.Attack"),
                    GameAssembly.GetType("ZX.Commands.Stop"),
                    GameAssembly.GetType("ZX.Commands.Hold"),
                    GameAssembly.GetType("ZX.Commands.UseItem_Grenade")
                };
            }
            if (_groupRoutedCommandTypes == null) return false;
            for (int i = 0; i < _groupRoutedCommandTypes.Length; i++)
            {
                var t = _groupRoutedCommandTypes[i];
                if (t != null && t.IsInstanceOfType(command)) return true;
            }
            return false;
        }

        /// <summary>
        /// Inspects a ZX.Commands.EntityBonusCreation command and extracts its slot, entity type,
        /// whether the rewarded entity is a Structure (placed on map) vs Unit (spawned at CommandCenter),
        /// and its DXEntityTemplate.
        /// </summary>
        public static bool TryGetEntityBonusInfo(object command, out int slot, out Type entityType, out bool isStructure, out DXEntityTemplate template)
        {
            slot = 0;
            entityType = null;
            isStructure = false;
            template = null;
            if (command == null || _typeEntityBonusCreation == null || !_typeEntityBonusCreation.IsInstanceOfType(command))
                return false;

            try
            {
                if (_propBonusCmdNSlot != null)
                    slot = Convert.ToInt32(_propBonusCmdNSlot.GetValue(command));
                template = _propBonusCmdTemplate?.GetValue(command) as DXEntityTemplate;
                DXEntity entity = template?.Entity ?? (_propBonusCmdEntity?.GetValue(command) as DXEntity);
                if (entity != null)
                {
                    entityType = entity.GetType();
                    isStructure = TypeStructure != null && TypeStructure.IsInstanceOfType(entity);
                }
            }
            catch { }
            return true;
        }

        public static bool TryGetExtractSlotInfo(object command, out int slot)
        {
            slot = 0;
            if (command == null || _typeExtractFromSlot == null || !_typeExtractFromSlot.IsInstanceOfType(command))
                return false;
            try
            {
                if (_propExtractCmdNSlot != null)
                    slot = Convert.ToInt32(_propExtractCmdNSlot.GetValue(command));
            }
            catch { }
            return true;
        }

        [ThreadStatic]
        private static DXEntity _activeContractMercenaryInn;

        public static DXEntity ActiveContractMercenaryInn
        {
            get => _activeContractMercenaryInn;
            set => _activeContractMercenaryInn = value;
        }

        public static bool IsTheInnEntity(object entity)
        {
            return entity != null && TypeTheInn != null && TypeTheInn.IsInstanceOfType(entity);
        }

        public static DXEntity GetLocalSelectedTheInn()
        {
            if (TypeTheInn == null || _fieldSelectedList == null) return null;
            foreach (object item in EnumerateSelectedComponents())
            {
                DXEntity ent = (item as DXComponent)?.Entity;
                if (ent != null && !ent.Disposed && TypeTheInn.IsInstanceOfType(ent))
                    return ent;
            }
            return null;
        }

        public static IList GetTheInnMercenaryTemplates(object innEntity)
        {
            if (!IsTheInnEntity(innEntity) || _mTheInnGetMercenaries == null) return null;
            try { return _mTheInnGetMercenaries.Invoke(innEntity, null) as IList; }
            catch { return null; }
        }

        public static int GetTheInnMercenaryCount(object innEntity)
        {
            IList list = GetTheInnMercenaryTemplates(innEntity);
            return list != null ? list.Count : -1;
        }

        public static bool TriggerFastTheInnMercenaries(object innEntity)
        {
            if (!IsTheInnEntity(innEntity) || TypeTheInn == null) return false;
            try
            {
                var propTimeActive = TypeTheInn.GetProperty("TimeActive", BindingFlags.Public | BindingFlags.Instance);
                var propBuilt = TypeTheInn.GetProperty("GameTimeBuilt", BindingFlags.Public | BindingFlags.Instance);
                if (propTimeActive == null) return false;

                int intervalDays = 3;
                object dp = _methodEntityGetDefaultParams?.Invoke(innEntity, null);
                var propInterval = dp?.GetType().GetProperty("NDaysNewMercenariesInterval", BindingFlags.Public | BindingFlags.Instance);
                if (propInterval != null)
                {
                    int val = Convert.ToInt32(propInterval.GetValue(dp, null));
                    if (val > 0) intervalDays = val;
                }

                if (propBuilt != null)
                {
                    int curBuilt = Convert.ToInt32(propBuilt.GetValue(innEntity, null));
                    if (curBuilt == 0)
                        propBuilt.SetValue(innEntity, Math.Max(1, DXGame.Current?.GameTimeInt ?? 1), null);
                }

                // NDaysSinceLastMercenaries = (TimeActive / 1.4) / 60000.0 -> intervalDays * 85000 guarantees >= intervalDays.
                int targetTimeActive = intervalDays * 85000;
                propTimeActive.SetValue(innEntity, targetTimeActive, null);
                ModLogger.Info($"[GameBridge] TriggerFastTheInnMercenaries: intervalDays={intervalDays}, set TimeActive={targetTimeActive}");
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] TriggerFastTheInnMercenaries failed: {ex.Message}");
                return false;
            }
        }

        public static bool TryGetContractMercenaryInfo(object command, object executor, out int slot, out Type entityType, out DXEntityTemplate template, out int innMercCount)
        {
            slot = 0;
            entityType = null;
            template = null;
            innMercCount = -1;
            if (command == null || _typeContractMercenary == null || !_typeContractMercenary.IsInstanceOfType(command))
                return false;

            DXEntity prevInn = _activeContractMercenaryInn;
            if (executor is DXEntity execEnt && IsTheInnEntity(execEnt))
                _activeContractMercenaryInn = execEnt;
            try
            {
                if (_propMercCmdNSlot != null)
                    slot = Convert.ToInt32(_propMercCmdNSlot.GetValue(command));

                DXEntity activeInn = _activeContractMercenaryInn;
                if (activeInn != null)
                    innMercCount = GetTheInnMercenaryCount(activeInn);

                // Vanilla ContractMercenary.get_EntityTemplate() indexes list[NSlot - 1] whenever
                // list.Count >= NSlot, so NSlot <= 0 triggers ArgumentOutOfRangeException on list[-1].
                if (slot <= 0 || (innMercCount >= 0 && slot > innMercCount))
                    return true;

                template = _propMercCmdTemplate?.GetValue(command) as DXEntityTemplate;
                DXEntity entity = template?.Entity ?? (_propMercCmdEntity?.GetValue(command) as DXEntity);
                if (entity != null)
                    entityType = entity.GetType();
            }
            catch { }
            finally
            {
                _activeContractMercenaryInn = prevInn;
            }
            return true;
        }

        public static bool TryResolveSafeContractMercenaryTemplate(object command, out DXEntityTemplate safeTemplate)
        {
            safeTemplate = null;
            if (command == null || _typeContractMercenary == null || !_typeContractMercenary.IsInstanceOfType(command))
                return false;

            try
            {
                int slot = _propMercCmdNSlot != null ? Convert.ToInt32(_propMercCmdNSlot.GetValue(command)) : 0;
                if (slot <= 0)
                    return true;

                DXEntity inn = _activeContractMercenaryInn;
                if (inn == null)
                {
                    var mGetInn = _typeContractMercenary.GetProperty("Inn", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                    inn = mGetInn?.Invoke(command, null) as DXEntity;
                }
                if (inn == null || inn.Disposed)
                    return true;

                IList mercList = GetTheInnMercenaryTemplates(inn);
                if (mercList == null || slot > mercList.Count)
                    return true;

                safeTemplate = mercList[slot - 1] as DXEntityTemplate;
                return true;
            }
            catch
            {
                return true;
            }
        }

        public static bool IsBuildOrBonusCommand(object command, out bool isBonus)
        {
            isBonus = false;
            if (command == null) return false;
            if (TryGetEntityBonusInfo(command, out _, out Type entityType, out bool isStructure, out _))
            {
                if (isStructure && entityType != null)
                {
                    isBonus = true;
                    return true;
                }
                // Bonus Units (TargetType.None) spawn next to the CommandCenter; they are NOT placed via PlaceBuilding.
                return false;
            }
            return string.Equals(command.GetType().Name, "Build", StringComparison.Ordinal);
        }

        public static int GetBonusEntityCount(Type entityType, out DXEntityTemplate matchedTemplate)
        {
            matchedTemplate = null;
            if (entityType == null) return 0;
            object ls = CurrentLevelState;
            var bonusDict = ls != null ? GetLevelProperty("BonusEntityTemplates")?.GetValue(ls) as Dictionary<ulong, int> : null;
            if (bonusDict == null || bonusDict.Count == 0) return 0;

            DXEntityTemplate directTpl = GetEntityTemplateByType(entityType);
            if (directTpl != null && bonusDict.TryGetValue(directTpl.ID, out int directCount) && directCount > 0)
            {
                matchedTemplate = directTpl;
                return directCount;
            }

            var proj = DXProject.Current;
            if (proj == null) return 0;
            foreach (var kv in bonusDict)
            {
                if (kv.Value <= 0) continue;
                var tpl = proj.EntityTemplateFromID(kv.Key);
                if (tpl?.Entity != null && tpl.Entity.GetType() == entityType)
                {
                    matchedTemplate = tpl;
                    return kv.Value;
                }
            }
            return 0;
        }

        public static void ConsumeBonusEntityByType(Type entityType)
        {
            if (entityType == null || _methodLevelStateConsumeBonus == null) return;
            object ls = CurrentLevelState;
            if (ls == null) return;
            if (GetBonusEntityCount(entityType, out DXEntityTemplate tpl) > 0 && tpl != null)
            {
                try { _methodLevelStateConsumeBonus.Invoke(ls, new object[] { tpl }); }
                catch (Exception ex) { ModLogger.Warn($"[GameBridge] ConsumeBonusEntityByType({entityType.Name}) failed: {ex.Message}"); }
            }
        }

        public static void CancelPendingBonusCommandIfType(Type buildingType)
        {
            if (buildingType == null) return;
            object pending = GetPendingCommandOnLevelSys();
            if (pending == null) return;
            if (!TryGetEntityBonusInfo(pending, out _, out Type pendingEntityType, out _, out _))
                return;
            // After ConsumeBonusEntityByType removes the last charge, pendingEntityType may already be null or still point at a different shifted slot.
            if (pendingEntityType == null || pendingEntityType == buildingType)
            {
                object levelSys = LevelSystemInstance;
                if (levelSys != null && _methodLevelSysClearPendingMode != null)
                {
                    try { _methodLevelSysClearPendingMode.Invoke(levelSys, null); } catch { }
                }
            }
        }

        public static IList GetAvailableCommandsForEntity(object entity) => GetAvailableCommandsForExecutor(entity);

        private static IList GetAvailableCommandsForExecutor(object executor)
        {
            if (executor == null || _propCmdableAvailableCmds == null) return null;
            object cmdable = GetCommandableObject(executor);
            if (cmdable == null) return null;
            try { return _propCmdableAvailableCmds.GetValue(cmdable, null) as IList; }
            catch { return null; }
        }

        /// <summary>
        /// Resolves the command object for ExecuteSingleOrBatchCommand replay.
        /// Handles slot-indexed commands (EntityBonusCreation, ExtractFromUnitHolderSlot, ContractMercenary)
        /// whose base ZXCommand.ID collides across all slots.
        /// </summary>
        public static object ResolveReplayCommand(int commandId, string typeOrSlotTag, byte slotIndex, object executor, out bool isSlotSensitive)
        {
            isSlotSensitive = !string.IsNullOrEmpty(typeOrSlotTag);
            if (!isSlotSensitive)
                return GetCommandById(commandId);

            if (string.Equals(typeOrSlotTag, "ZX.Commands.ExtractFromUnitHolderSlot", StringComparison.Ordinal))
            {
                IList avail = GetAvailableCommandsForExecutor(executor);
                if (avail != null)
                {
                    foreach (object c in avail)
                    {
                        if (TryGetExtractSlotInfo(c, out int s) && s == slotIndex)
                            return c;
                    }
                }
                object game = ZXGameInstance;
                var allCmds = game != null ? _fieldZXGameAllCommands?.GetValue(game) as IList : null;
                if (allCmds != null)
                {
                    foreach (object c in allCmds)
                    {
                        if (TryGetExtractSlotInfo(c, out int s) && s == slotIndex)
                            return c;
                    }
                }
                return null;
            }

            if (string.Equals(typeOrSlotTag, ObfuscatedSymbols.Type_ContractMercenary, StringComparison.Ordinal)
                || typeOrSlotTag.StartsWith("ZX.Commands.ContractMercenary|", StringComparison.Ordinal))
            {
                string expectedTypeName = null;
                int sep = typeOrSlotTag.IndexOf('|');
                if (sep >= 0 && sep + 1 < typeOrSlotTag.Length)
                    expectedTypeName = typeOrSlotTag.Substring(sep + 1);

                IList avail = GetAvailableCommandsForExecutor(executor);
                int innMercCount = GetTheInnMercenaryCount(executor);
                if (avail != null && slotIndex > 0)
                {
                    object fallbackTypeMatch = null;
                    foreach (object c in avail)
                    {
                        if (!TryGetContractMercenaryInfo(c, executor, out int slot, out Type mercType, out _, out int liveCount))
                            continue;
                        if (liveCount >= 0)
                            innMercCount = liveCount;
                        if (slot <= 0 || mercType == null)
                            continue;

                        bool typeMatches = string.IsNullOrEmpty(expectedTypeName)
                            || string.Equals(mercType.FullName, expectedTypeName, StringComparison.Ordinal);
                        if (!typeMatches)
                            continue;

                        if (slot == slotIndex)
                            return c;
                        if (fallbackTypeMatch == null && !string.IsNullOrEmpty(expectedTypeName))
                            fallbackTypeMatch = c;
                    }
                    if (fallbackTypeMatch != null)
                        return fallbackTypeMatch;
                }

                ulong execId = (executor as DXEntity)?.ID ?? 0UL;
                ModLogger.Warn($"[GameBridge] ResolveReplayCommand(ContractMercenary) unavailable: executor={execId}, slot={slotIndex}, expectedType={expectedTypeName ?? "<any>"}, innMercCount={innMercCount}.");
                return null;
            }

            // EntityBonusCreation for a Unit (or any non-structure bonus): CommandCenter.#=z0rkMLsM_Q666() populates slots 1..12.
            IList ccAvail = GetAvailableCommandsForExecutor(executor)
                ?? GetAvailableCommandsForExecutor(GetCommandCenterEntity());
            if (ccAvail == null) return null;

            object fallbackMatch = null;
            foreach (object c in ccAvail)
            {
                if (!TryGetEntityBonusInfo(c, out int slot, out Type entityType, out bool isStructure, out _))
                    continue;
                if (isStructure || entityType == null)
                    continue;
                if (!string.Equals(entityType.FullName, typeOrSlotTag, StringComparison.Ordinal))
                    continue;
                if (slot == slotIndex)
                    return c;
                if (fallbackMatch == null)
                    fallbackMatch = c;
            }
            return fallbackMatch;
        }

        public static string DescribeCommandRejection(object command, object executor)
        {
            if (command == null) return string.Empty;
            try
            {
                bool vis = _methodIsVisibleCmd == null || (bool)_methodIsVisibleCmd.Invoke(command, new[] { executor });
                bool can = _methodCanExecuteCmd == null || (bool)_methodCanExecuteCmd.Invoke(command, new[] { executor });
                string err = _fCmdErrorMsg?.GetValue(command) as string;
                object dp = _mCmdGetDefaultParams?.Invoke(command, null);
                Type bReq = dp != null ? _pCmdDefBuildingRequisite?.GetValue(dp, null) as Type : null;
                Type cReq = dp?.GetType().GetProperty("CommandRequisite", BindingFlags.Public | BindingFlags.Instance)?.GetValue(dp, null) as Type;
                return $" [vis={vis}, can={can}, err={err ?? "null"}, bReq={bReq?.Name ?? "none"}, cReq={cReq?.Name ?? "none"}]";
            }
            catch { return string.Empty; }
        }

        public static bool CanExecuteCommandOnEntity(object command, object executor)
        {
            if (command == null || executor == null) return true;
            try
            {
                if (_methodIsVisibleCmd != null && !(bool)_methodIsVisibleCmd.Invoke(command, new[] { executor }))
                    return false;
                if (_methodCanExecuteCmd != null && !(bool)_methodCanExecuteCmd.Invoke(command, new[] { executor }))
                    return false;
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] CanExecuteCommandOnEntity failed: {ex.Message}");
                return true;
            }
        }

        public static bool IsResourceSpendingCommand(object command, object executor)
        {
            if (command == null) return false;
            if (_typeCreateEntityCommand != null && _typeCreateEntityCommand.IsInstanceOfType(command))
                return true;
            if (executor != null && _methodGetResourcesCost != null)
            {
                try
                {
                    if (_methodGetResourcesCost.Invoke(command, new[] { executor }) != null)
                        return true;
                }
                catch { }
            }
            return false;
        }

        public static Rectangle ResolveBuildCellArea(DXEntity templateEntity, PointF targetPos, SizeF targetSize, PointF targetOrigin)
        {
            PointF effectivePos = targetPos != PointF.Empty ? targetPos : targetOrigin;
            int tw = (int)Math.Round(targetSize.Width);
            int th = (int)Math.Round(targetSize.Height);
            if (tw <= 0 || th <= 0)
            {
                if (templateEntity == null) return Rectangle.Empty;
                tw = Math.Max(1, templateEntity.CellArea.Width);
                th = Math.Max(1, templateEntity.CellArea.Height);
                int xFallback = (int)Math.Floor(effectivePos.X);
                int yFallback = (int)Math.Floor(effectivePos.Y);
                return new Rectangle(xFallback, yFallback, tw, th);
            }

            Rectangle targetRect = Rectangle.Truncate(new RectangleF(
                effectivePos.X - targetSize.Width * 0.5f,
                effectivePos.Y - targetSize.Height * 0.5f,
                targetSize.Width,
                targetSize.Height));

            if (templateEntity != null)
            {
                int tmplArea = templateEntity.CellArea.Width * templateEntity.CellArea.Height;
                int targetArea = targetRect.Width * targetRect.Height;
                if (tmplArea != targetArea)
                {
                    return new Rectangle(
                        targetRect.X,
                        targetRect.Y,
                        Math.Max(1, templateEntity.CellArea.Width),
                        Math.Max(1, templateEntity.CellArea.Height));
                }
            }
            return targetRect;
        }

        public static bool CanPlaceBuildingAtTarget(Type buildingType, PointF targetPos, SizeF targetSize, PointF targetOrigin)
        {
            if (buildingType == null) return false;
            if (string.Equals(buildingType.Name, "TheGreatTelescope", StringComparison.Ordinal))
                return true;

            try
            {
                DXGame zxGame = ZXGameInstance;
                if (zxGame == null || _methodGetTemplateFromEntityType == null || _methodEntityCanPlaceAtCell == null)
                    return true;

                var template = _methodGetTemplateFromEntityType.Invoke(zxGame, new object[] { buildingType }) as DXEntityTemplate;
                DXEntity templateEntity = template?.Entity;
                if (templateEntity == null) return true;

                _methodEntityUpdateCellPosition?.Invoke(templateEntity, null);

                Rectangle rect = ResolveBuildCellArea(templateEntity, targetPos, targetSize, targetOrigin);
                if (rect.Width <= 0 || rect.Height <= 0)
                    return true;

                PointF origPos = templateEntity.Position;
                SizeF origSize = templateEntity.Size;
                Point origCell = templateEntity.Cell;
                try
                {
                    templateEntity.CellArea = rect;
                    for (int x = rect.Left; x < rect.Right; x++)
                    {
                        for (int y = rect.Top; y < rect.Bottom; y++)
                        {
                            if (!(bool)_methodEntityCanPlaceAtCell.Invoke(templateEntity, new object[] { x, y }))
                                return false;
                        }
                    }
                    return true;
                }
                finally
                {
                    templateEntity.Size = origSize;
                    templateEntity.Position = origPos;
                    templateEntity.Cell = origCell;
                    _methodEntityUpdateCellPosition?.Invoke(templateEntity, null);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] CanPlaceBuildingAtTarget({buildingType.Name}) failed: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Temporarily removes the local OCellAreaCursor's preview energy-grid +1 so replaying
        /// remote PlaceBuilding commands sees the deterministic shared energy grid rather than
        /// whichever power building the local player happens to be hovering.
        /// </summary>
        public static object SuspendLocalBuildCursorEnergyPreview()
        {
            if (_fieldLevelSysBuildAreaCursor == null || _fieldCursorLastEnergyCell == null || _methodCursorClearEnergyPreview == null)
                return null;
            int mainId = Determinism.LockstepClock.MainThreadId;
            if (mainId != 0 && System.Threading.Thread.CurrentThread.ManagedThreadId != mainId)
                return null;
            try
            {
                object levelSys = LevelSystemInstance;
                if (levelSys == null) return null;
                object cursor = _fieldLevelSysBuildAreaCursor.GetValue(levelSys);
                if (cursor == null) return null;
                if (_fieldCursorLastEnergyCell.GetValue(cursor) == null)
                    return null;
                _methodCursorClearEnergyPreview.Invoke(cursor, null);
                return cursor;
            }
            catch
            {
                return null;
            }
        }

        public static void RestoreLocalBuildCursorEnergyPreview(object cursor)
        {
            if (cursor == null || _methodCursorUpdateVisual == null) return;
            int mainId = Determinism.LockstepClock.MainThreadId;
            if (mainId != 0 && System.Threading.Thread.CurrentThread.ManagedThreadId != mainId)
                return;
            try
            {
                if (cursor is DXObject obj && obj.Scene != null)
                    _methodCursorUpdateVisual.Invoke(cursor, null);
            }
            catch { }
        }

        // ====================================================================
        // Phase 2 & 3 Async Delta Patching Helpers (ProdQueues / Structures /
        // Combat+Move Targets / Missing Dynamic Entity Respawn)
        // ====================================================================

        public static int[] ReadOrdersQueuedCommandIds(DXEntity entity)
        {
            if (entity == null || entity.Disposed) return null;
            IList orders = GetOrdersQueued(GetCommandableObject(entity));
            if (orders == null || orders.Count == 0) return null;
            var ids = new int[orders.Count];
            int n = 0;
            for (int i = 0; i < orders.Count; i++)
            {
                object item = orders[i];
                if (item == null) continue;
                ids[n++] = GetOrderCommandId(item);
            }
            if (n == 0) return null;
            if (n < ids.Length) Array.Resize(ref ids, n);
            return ids;
        }

        public static bool ApplyOrdersQueuedCommandIds(DXEntity entity, int[] commandIds)
        {
            if (entity == null || entity.Disposed || _typeZXOrder == null || _ctorZXOrder == null)
                return false;
            object cmdable = GetCommandableObject(entity);
            IList orders = GetOrdersQueued(cmdable);
            if (orders == null) return false;

            EnsureCommandTargetMeta();
            if (_typeZXCommandTargetRef == null) return false;

            if (_propZXOrderCommand == null)
                _propZXOrderCommand = _typeZXOrder.GetProperty("Command", BindingFlags.Public | BindingFlags.Instance);
            if (_methodTrainReserve == null && TypeTrain != null)
                _methodTrainReserve = AccessTools.Method(TypeTrain, "#=zEjiaQKLJuj2V5aXjiw==", Type.EmptyTypes);
            if (_methodTrainUnreserve == null && TypeTrain != null)
                _methodTrainUnreserve = AccessTools.Method(TypeTrain, "#=z6YYk1ExkWvhWXnV3NQ==", Type.EmptyTypes);

            bool pushedContext = false;
            if (Sync.CoopSessionManager.IsMultiFactionActive && Sync.FactionContextManager.IsInitialized)
            {
                byte owner = Sync.FactionOwnershipTracker.ResolveOrAssignOwner(entity);
                if (owner != 0)
                {
                    Sync.FactionContextManager.PushContext(owner);
                    pushedContext = true;
                }
            }

            try
            {
                lock (orders.SyncRoot)
                {
                    if (_propZXOrderCommand != null && _methodTrainUnreserve != null && TypeTrain != null)
                    {
                        for (int i = 0; i < orders.Count; i++)
                        {
                            object existingOrder = orders[i];
                            object existingCmd = existingOrder != null ? _propZXOrderCommand.GetValue(existingOrder) : null;
                            if (existingCmd != null && TypeTrain.IsInstanceOfType(existingCmd))
                                _methodTrainUnreserve.Invoke(existingCmd, null);
                        }
                    }

                    orders.Clear();
                    if (commandIds != null && commandIds.Length > 0)
                    {
                        object defaultTarget = Activator.CreateInstance(_typeZXCommandTargetRef);
                        int limit = Math.Min(commandIds.Length, 4);
                        for (int i = 0; i < limit; i++)
                        {
                            object cmd = GetCommandById(commandIds[i]);
                            if (cmd == null) continue;
                            object zxOrder = _ctorZXOrder.Invoke(new[] { cmd, defaultTarget });
                            if (zxOrder != null)
                            {
                                orders.Add(zxOrder);
                                if (_methodTrainReserve != null && TypeTrain != null && TypeTrain.IsInstanceOfType(cmd))
                                    _methodTrainReserve.Invoke(cmd, null);
                            }
                        }
                    }
                }
                if (pushedContext)
                    Sync.FactionContextManager.RecalculateAllFactions();
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] ApplyOrdersQueuedCommandIds({entity.ID}) failed: {ex.Message}");
                return false;
            }
            finally
            {
                if (pushedContext)
                    Sync.FactionContextManager.PopContext();
            }
        }

        public static void RefreshCommandPanel()
        {
            try
            {
                object levelSys = LevelSystemInstance;
                if (levelSys != null)
                    _methodLevelSysRefreshCmdPanel?.Invoke(levelSys, null);
            }
            catch { }
        }

        public static bool TryCaptureStructureMeta(
            DXEntity entity,
            out ulong templateId,
            out int cellX,
            out int cellY,
            out byte width,
            out byte height,
            out float buildFactor)
        {
            templateId = 0UL;
            cellX = 0;
            cellY = 0;
            width = 1;
            height = 1;
            buildFactor = 1f;
            if (entity == null || entity.Disposed || !IsStructureType(entity.GetType()))
                return false;
            if (entity.Template == null) return false;

            templateId = entity.Template.ID;
            var ca = entity.CellArea;
            cellX = ca.X;
            cellY = ca.Y;
            width = (byte)Math.Max(1, Math.Min(255, ca.Width));
            height = (byte)Math.Max(1, Math.Min(255, ca.Height));
            if (_typeCBuildable != null && _propCBuildableFactor != null)
            {
                var comps = entity.Components;
                if (comps != null)
                {
                    for (int i = 0; i < comps.Count; i++)
                    {
                        if (comps[i] != null && _typeCBuildable.IsInstanceOfType(comps[i]))
                        {
                            buildFactor = Convert.ToSingle(_propCBuildableFactor.GetValue(comps[i]));
                            break;
                        }
                    }
                }
            }
            return true;
        }

        public static bool TrySetEntityLifeAbsolute(DXEntity entity, int life)
        {
            object lifeObj = GetLifeObject(entity);
            if (lifeObj == null) return false;
            Type t = lifeObj.GetType();
            try
            {
                if (!_lifePropCache.TryGetValue(t, out PropertyInfo lifeProp))
                {
                    lifeProp = t.GetProperty("Life", BindingFlags.Public | BindingFlags.Instance);
                    _lifePropCache[t] = lifeProp;
                }
                if (lifeProp == null || !lifeProp.CanWrite) return false;
                lifeProp.SetValue(lifeObj, Math.Max(0, life));
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool PlaceSyncedStructure(
            ulong hostEntityId,
            ulong templateId,
            int cellX,
            int cellY,
            int width,
            int height,
            int life,
            float buildFactor,
            byte ownerPlayerId = 0)
        {
            var game = DXGame.Current;
            var proj = DXProject.Current;
            if (game == null || proj == null || hostEntityId == 0UL || templateId == 0UL)
                return false;
            if (game.GetEntityByID(hostEntityId) != null)
                return false;

            DXEntityTemplate tpl = proj.EntityTemplateFromID(templateId);
            if (tpl == null) return false;

            Determinism.LockstepClock.EnterVisualVfxContext();
            try
            {
                DXEntity ent = tpl.CreateInstance() as DXEntity;
                if (ent == null || !IsStructureType(ent.GetType()))
                    return false;

                ent.ID = hostEntityId;
                if (Sync.CoopSessionManager.IsMultiFactionActive && ownerPlayerId != 0)
                    Sync.FactionOwnershipTracker.SetOwner(hostEntityId, ownerPlayerId);
                if (_propEntityRelation != null)
                    _propEntityRelation.SetValue(ent, Enum.ToObject(_propEntityRelation.PropertyType, 0)); // Friend = 0
                if (_propEntityTeam != null)
                    _propEntityTeam.SetValue(ent, Enum.ToObject(_propEntityTeam.PropertyType, 0)); // Player = 0

                ent.CellArea = new Rectangle(cellX, cellY, Math.Max(1, width), Math.Max(1, height));
                _methodEntityUpdateCellPosition?.Invoke(ent, null);

                bool isBeingBuilt = buildFactor < 0.999f && _typeCBuildable != null && _propCBuildableMode != null && _propCBuildableFactor != null;
                if (isBeingBuilt)
                {
                    var cBuildable = Activator.CreateInstance(_typeCBuildable) as DXComponent;
                    if (cBuildable != null)
                    {
                        _propCBuildableMode.SetValue(cBuildable, Enum.Parse(_propCBuildableMode.PropertyType, "Building"));
                        _propCBuildableFactor.SetValue(cBuildable, Math.Max(0f, Math.Min(0.99f, buildFactor)));
                        ent.AddComponent(cBuildable);
                    }
                    _propStructureIsBeingBuilt?.SetValue(ent, true);
                }
                else
                {
                    _propStructureIsBeingBuilt?.SetValue(ent, false);
                }

                _methodStructureUpdateEnabled?.Invoke(ent, null);
                ent.CreateSceneObject();

                object ls = CurrentLevelState;
                if (ls != null)
                    _methodLevelStateRecordEntityCreated?.Invoke(ls, new object[] { ent });

                ent.InvokeAddToScene();
                FlushStartFramePostMethods();

                if (life > 0)
                    TrySetEntityLifeAbsolute(ent, life);

                if (TypeBuilding != null && TypeBuilding.IsInstanceOfType(ent))
                    _methodBuildingRecalcResources?.Invoke(ent, null);

                if (ent.SceneObject != null && ent.SceneObject.Animation != null)
                {
                    DXClip clip = ent.SceneObject.Animation.NewClip();
                    DXTransition tr = clip.NewTransition(200, -1f);
                    tr.RangeCenter = new DXRange<PointF?>(ent.SceneObject.Center.Add(0f, -1f), ent.SceneObject.Center);
                    tr.RangeOpacity = new DXRange<float?>(0f, ent.SceneObject.Opacity);
                    clip.OnFinished += delegate { try { clip.Clear(); } catch { } };
                }

                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] PlaceSyncedStructure(id={hostEntityId}, tpl={templateId}) failed: {ex.Message}");
                return false;
            }
            finally
            {
                Determinism.LockstepClock.ExitVisualVfxContext();
            }
        }

        public static bool DestroySyncedExtraStructure(DXEntity entity)
        {
            if (entity == null || entity.Disposed || !IsStructureType(entity.GetType()))
                return false;
            if (ReferenceEquals(entity, GetCommandCenterEntity()))
                return false;

            Determinism.LockstepClock.EnterVisualVfxContext();
            try
            {
                entity.RemoveFromScene();
                entity.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] DestroySyncedExtraStructure(id={entity.ID}) failed: {ex.Message}");
                return false;
            }
            finally
            {
                Determinism.LockstepClock.ExitVisualVfxContext();
            }
        }

        private static object GetBDataFromEntity(DXEntity entity)
        {
            if (entity == null || _mGetEntityBehaviour == null || !_mGetEntityBehaviour.DeclaringType.IsInstanceOfType(entity))
                return null;
            object cbehav = _mGetEntityBehaviour.Invoke(entity, null);
            if (cbehav == null) return null;
            if (_propCBehavBehaviour == null)
                _propCBehavBehaviour = cbehav.GetType().GetProperty("Behaviour", BindingFlags.Public | BindingFlags.Instance);
            object behav = _propCBehavBehaviour?.GetValue(cbehav);
            if (behav == null) return null;
            if (_propBehavData == null)
                _propBehavData = behav.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            return _propBehavData?.GetValue(behav);
        }

        private static bool TrySetEntityTargetRefOnBData(object bdata, ulong targetEntityId)
        {
            if (bdata == null) return false;
            if (_fieldBDataTargetRef == null)
                _fieldBDataTargetRef = bdata.GetType().GetField(ObfuscatedSymbols.Field_BData_TargetRef, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_fieldBDataTargetRef == null) return false;

            Type refType = _fieldBDataTargetRef.FieldType;
            object refBox = Activator.CreateInstance(refType);
            if (targetEntityId != 0UL)
            {
                DXEntity targetEnt = DXGame.Current?.GetEntityByID(targetEntityId);
                if (targetEnt == null || targetEnt.Disposed)
                    return false;
                if (TypeZXEntity != null && !TypeZXEntity.IsInstanceOfType(targetEnt))
                {
                    ModLogger.Warn($"[GameBridge] TrySetEntityTargetRefOnBData skipped non-ZXEntity target id={targetEntityId} ({targetEnt.GetType().Name}).");
                    return false;
                }
                PropertyInfo pEnt = refType.GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
                if (pEnt == null) return false;
                pEnt.SetValue(refBox, targetEnt);
            }
            _fieldBDataTargetRef.SetValue(bdata, refBox);
            return true;
        }

        public static void GetEntityCombatAndMoveTargets(DXEntity entity, out ulong targetEntityId, out float moveX, out float moveY)
        {
            if (_fastGetCombatAndMoveTargets != null && entity != null && !entity.Disposed)
            {
                _fastGetCombatAndMoveTargets(entity, out targetEntityId, out moveX, out moveY);
                return;
            }

            targetEntityId = 0UL;
            moveX = float.NaN;
            moveY = float.NaN;
            if (entity == null || entity.Disposed) return;

            try
            {
                object bdata = GetBDataFromEntity(entity);
                if (bdata != null)
                {
                    if (_fieldBDataTargetRef == null)
                        _fieldBDataTargetRef = bdata.GetType().GetField(ObfuscatedSymbols.Field_BData_TargetRef, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    object boxedRef = _fieldBDataTargetRef?.GetValue(bdata);
                    if (boxedRef != null)
                    {
                        if (_propTargetRefId == null)
                            _propTargetRefId = boxedRef.GetType().GetProperty("IDEntity", BindingFlags.Public | BindingFlags.Instance);
                        if (_propTargetRefId != null)
                            targetEntityId = Convert.ToUInt64(_propTargetRefId.GetValue(boxedRef));
                    }
                }

                if (_mGetEntityMovable != null && _mGetEntityMovable.DeclaringType.IsInstanceOfType(entity))
                {
                    object movable = _mGetEntityMovable.Invoke(entity, null);
                    if (movable != null && _propMovableTargetPosProp != null)
                    {
                        object val = _propMovableTargetPosProp.GetValue(movable);
                        if (val is PointF pt)
                        {
                            moveX = pt.X;
                            moveY = pt.Y;
                        }
                    }
                }
            }
            catch { }
        }

        public static bool TryAlignEntityCombatAndMoveTarget(
            DXEntity entity,
            ulong hostTargetId,
            float hostMoveX,
            float hostMoveY,
            bool alignMoveTarget = true,
            bool onlyIfLocalIdle = false)
        {
            if (entity == null || entity.Disposed) return false;
            bool changed = false;
            try
            {
                GetEntityCombatAndMoveTargets(entity, out ulong localTargetId, out float localMoveX, out float localMoveY);
                if (onlyIfLocalIdle && localTargetId != 0UL)
                    return false;

                if (localTargetId != hostTargetId)
                {
                    object bdata = GetBDataFromEntity(entity);
                    if (bdata != null && TrySetEntityTargetRefOnBData(bdata, hostTargetId))
                        changed = true;
                }

                if (!alignMoveTarget)
                    return changed;

                bool hostHasMove = !float.IsNaN(hostMoveX) && !float.IsNaN(hostMoveY) && !float.IsInfinity(hostMoveX) && !float.IsInfinity(hostMoveY);
                bool localHasMove = !float.IsNaN(localMoveX) && !float.IsNaN(localMoveY);
                bool moveDiff = hostHasMove != localHasMove;
                if (hostHasMove && localHasMove)
                {
                    float dx = hostMoveX - localMoveX;
                    float dy = hostMoveY - localMoveY;
                    if (dx * dx + dy * dy >= 0.25f)
                        moveDiff = true;
                }

                if (moveDiff && _mGetEntityMovable != null && _mGetEntityMovable.DeclaringType.IsInstanceOfType(entity))
                {
                    object movable = _mGetEntityMovable.Invoke(entity, null);
                    if (movable != null && _propMovableTargetPosProp != null)
                    {
                        PointF? nextTarget = hostHasMove ? (PointF?)new PointF(hostMoveX, hostMoveY) : null;
                        _propMovableTargetPosProp.SetValue(movable, nextTarget);
                        (_propMovablePath?.GetValue(movable) as IList)?.Clear();
                        _fieldMovablePendingPath?.SetValue(movable, null);
                        changed = true;
                    }
                }
            }
            catch { }
            return changed;
        }

        public static bool TryCaptureDynamicSpawnMeta(DXEntity entity, out ulong templateId, out byte teamRaw, out ulong targetId)
        {
            templateId = 0UL;
            teamRaw = 0;
            targetId = 0UL;
            if (entity == null || entity.Disposed || entity.Template == null)
                return false;

            templateId = entity.Template.ID;
            if (_propEntityTeam != null)
            {
                try { teamRaw = Convert.ToByte(_propEntityTeam.GetValue(entity)); } catch { }
            }
            GetEntityCombatAndMoveTargets(entity, out targetId, out _, out _);
            return templateId != 0UL;
        }

        public static bool SpawnSyncedDynamicEntity(
            ulong hostEntityId,
            ulong templateId,
            float x,
            float y,
            int life,
            byte teamRaw,
            ulong targetId,
            byte ownerPlayerId = 0)
        {
            var game = DXGame.Current;
            var proj = DXProject.Current;
            if (game == null || proj == null || hostEntityId == 0UL || templateId == 0UL)
                return false;
            if (game.GetEntityByID(hostEntityId) != null)
                return false;

            DXEntityTemplate tpl = proj.EntityTemplateFromID(templateId);
            if (tpl == null) return false;

            Determinism.LockstepClock.EnterVisualVfxContext();
            try
            {
                DXEntity ent = tpl.CreateInstance() as DXEntity;
                if (ent == null) return false;

                ent.ID = hostEntityId;
                if (Sync.CoopSessionManager.IsMultiFactionActive && ownerPlayerId != 0)
                    Sync.FactionOwnershipTracker.SetOwner(hostEntityId, ownerPlayerId);
                if (_propEntityTeam != null)
                    _propEntityTeam.SetValue(ent, Enum.ToObject(_propEntityTeam.PropertyType, teamRaw));

                var pos = new PointF(
                    Determinism.LockstepClock.QuantizeCoord(x),
                    Determinism.LockstepClock.QuantizeCoord(y));
                ent.LastPosition = pos;
                ent.Position = pos;

                ent.InvokeAddToScene();
                FlushStartFramePostMethods();

                if (life > 0)
                    TrySetEntityLifeAbsolute(ent, life);

                if (targetId != 0UL)
                {
                    object bdata = GetBDataFromEntity(ent);
                    if (bdata != null)
                        TrySetEntityTargetRefOnBData(bdata, targetId);
                }

                if (teamRaw == 0)
                {
                    object ls = CurrentLevelState;
                    if (ls != null)
                        _methodLevelStateRecordEntityCreated?.Invoke(ls, new object[] { ent });
                }

                if (ent.SceneObject != null)
                {
                    bool isZombie = IsZombieType(ent.GetType());
                    bool hasFogGateComp = isZombie && HasComponentOfType(ent, TypeCInvisibleWhenNotWatched) && IsHideEnemiesIfNotWatched();
                    if (hasFogGateComp)
                    {
                        RefreshEntityFogVisibility(ent);
                    }
                    else
                    {
                        ent.SceneObject.Opacity = 0f;
                        if (ent.SceneObject.Animation != null)
                        {
                            DXClip clip = ent.SceneObject.Animation.NewClip();
                            DXTransition tr = clip.NewTransition(isZombie ? 200 : 350, -1f);
                            tr.RangeOpacity = new DXRange<float?>(0f, 1f);
                            clip.OnFinished += delegate { try { clip.Clear(); } catch { } };
                        }
                        else
                        {
                            ent.SceneObject.Opacity = 1f;
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] SpawnSyncedDynamicEntity(id={hostEntityId}, tpl={templateId}) failed: {ex.Message}");
                return false;
            }
            finally
            {
                Determinism.LockstepClock.ExitVisualVfxContext();
            }
        }

        private static bool HasComponentOfType(DXEntity entity, Type componentType)
        {
            if (entity == null || componentType == null || entity.Components == null)
                return false;
            for (int i = 0; i < entity.Components.Count; i++)
            {
                var c = entity.Components[i];
                if (c != null && componentType.IsInstanceOfType(c))
                    return true;
            }
            return false;
        }

        public static bool IsHideEnemiesIfNotWatched()
        {
            try
            {
                object ext = DXLevel.Current?.Extension ?? _methodLevelExtGetCurrent?.Invoke(null, null);
                if (ext != null && _propLevelExtHideEnemies != null)
                    return (bool)_propLevelExtHideEnemies.GetValue(ext);
            }
            catch { }
            return true;
        }

        /// <summary>
        /// Updates fog-gated visibility immediately on spawned/teleported zombies so enemies inside
        /// currently watched vision appear at full opacity instead of starting or sticking at 0f.
        /// </summary>
        public static void RefreshEntityFogVisibility(DXEntity entity)
        {
            if (entity == null || entity.Disposed || entity.SceneObject == null)
                return;

            try
            {
                bool hideIfNotWatched = IsHideEnemiesIfNotWatched();
                if (hideIfNotWatched && TypeCInvisibleWhenNotWatched != null && _methodCInvisibleUpdateOpacity != null && entity.Components != null)
                {
                    for (int i = 0; i < entity.Components.Count; i++)
                    {
                        var comp = entity.Components[i];
                        if (comp != null && TypeCInvisibleWhenNotWatched.IsInstanceOfType(comp))
                        {
                            _methodCInvisibleUpdateOpacity.Invoke(comp, null);
                            return;
                        }
                    }
                }

                if (!hideIfNotWatched && entity.SceneObject.Opacity < 0.01f)
                {
                    entity.SceneObject.Opacity = 1f;
                    entity.SceneObject.Visible = true;
                }
            }
            catch { }
        }

        public static void FlushStartFramePostMethods()
        {
            var game = DXGame.Current;
            var q = game?.PostMethods_OnStartFrame;
            if (q == null) return;

            bool prevRendering = game.RenderingFrame;
            bool prevDet = Determinism.LockstepClock.InDeterministicContext;
            game.RenderingFrame = false;
            Determinism.LockstepClock.InDeterministicContext = false;
            try
            {
                q.ExecuteAll();
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] FlushStartFramePostMethods failed: {ex.Message}");
            }
            finally
            {
                Determinism.LockstepClock.InDeterministicContext = prevDet;
                game.RenderingFrame = prevRendering;
            }
        }

        public static void ShowGroundIndicatorForCommand(object command, PointF worldPos, float scale = 1f)
        {
            if (command == null || _methodShowGroundIndicator == null) return;
            object levelSys = _mLevelSysCurrent?.Invoke(null, null);
            if (levelSys == null) return;

            Type t = command.GetType();
            if (!_cmdIndicatorColorCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("TargetIndicatorColor", BindingFlags.Public | BindingFlags.Instance);
                _cmdIndicatorColorCache[t] = prop;
            }
            if (prop == null) return;

            try
            {
                object colorVal = prop.GetValue(command);
                if (colorVal != null)
                    _methodShowGroundIndicator.Invoke(levelSys, new[] { worldPos, colorVal, scale });
            }
            catch { }
        }

        /// <summary>
        /// Scan the entity's public component list instead of HasComponent&lt;T&gt;() — instantiating a
        /// generic method with an internal type argument trips the CLR's type-access check.
        /// </summary>
        public static bool HasComponent(DXEntity entity, Type componentType)
        {
            return FindComponent(entity, componentType) != null;
        }

        public static DXComponent FindComponent(DXEntity entity, Type componentType)
        {
            if (entity == null || componentType == null) return null;
            var comps = entity.Components;
            if (comps == null) return null;
            for (int i = 0; i < comps.Count; i++)
            {
                if (IsComponentOfType(comps[i], componentType)) return comps[i];
            }
            return null;
        }

        private static bool IsComponentOfType(object component, Type componentType) => componentType.IsInstanceOfType(component);

        public static object GetCommandableTargetObject(object commandable)
        {
            if (commandable == null) return null;
            if (_fastGetCmdTarget != null && _fastGetCmdTargetDeclType != null && _fastGetCmdTargetDeclType.IsInstanceOfType(commandable))
            {
                try { return _fastGetCmdTarget(commandable); } catch { return null; }
            }
            Type t = commandable.GetType();
            if (!_cmdableTargetPropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("Target", BindingFlags.Public | BindingFlags.Instance);
                _cmdableTargetPropCache[t] = prop;
            }
            if (prop == null) return null;
            try { return prop.GetValue(commandable); } catch { return null; }
        }

        public static object GetBuildCommandForType(Type entityType) => GetCreateEntityCommandForType(ref _mBuildGetForType, "ZX.Commands.Build", entityType);
        public static object GetTrainCommandForType(Type entityType) => GetCreateEntityCommandForType(ref _mTrainGetForType, "ZX.Commands.Train", entityType);

        private static object GetCreateEntityCommandForType(ref MethodInfo cache, string typeName, Type entityType)
        {
            if (cache == null)
            {
                Type cmdType = GameAssembly?.GetType(typeName);
                if (cmdType == null) return null;
                cache = AccessTools.Method(cmdType, ObfuscatedSymbols.Method_BuildOrTrain_GetForType, new[] { typeof(Type) });
            }
            return cache?.Invoke(null, new object[] { entityType });
        }

        private static Type _typeZXCommandTargetRef;
        private static PropertyInfo _pSerPosition, _pSerSize, _pSerOrigin, _pSerEntityRef;
        private static Type _typeEntityRefClosed;
        private static PropertyInfo _pEntityRefEntity;
        private static PropertyInfo _pEntityRefId;
        private static FieldInfo _fEntityRefId;

        private static void EnsureCommandTargetMeta()
        {
            if (_typeZXCommandTargetRef != null) return;
            _typeZXCommandTargetRef = TypeZXCommandTarget;
            if (_typeZXCommandTargetRef == null) return;

            const BindingFlags F = BindingFlags.Public | BindingFlags.Instance;
            _pSerPosition = _typeZXCommandTargetRef.GetProperty("SerPosition", F);
            _pSerSize = _typeZXCommandTargetRef.GetProperty("SerSize", F);
            _pSerOrigin = _typeZXCommandTargetRef.GetProperty("SerOrigin", F);
            _pSerEntityRef = _typeZXCommandTargetRef.GetProperty("SerEntityRef", F);

            Type zxEntity = TypeZXEntity;
            if (zxEntity != null)
            {
                _typeEntityRefClosed = typeof(DXEntityRef<>).MakeGenericType(zxEntity);
                _pEntityRefEntity = _typeEntityRefClosed.GetProperty("Entity", F);
                _pEntityRefId = _typeEntityRefClosed.GetProperty("IDEntity", F);
                if (_pEntityRefId == null)
                    _fEntityRefId = _typeEntityRefClosed.GetField("IDEntity", F);
            }
        }

        public static void UnpackCommandTarget(object target, out ulong targetEntityId, out PointF pos, out SizeF size, out PointF origin)
        {
            targetEntityId = 0UL;
            pos = PointF.Empty;
            size = SizeF.Empty;
            origin = PointF.Empty;
            if (target == null) return;

            EnsureCommandTargetMeta();
            if (_typeZXCommandTargetRef == null) return;

            try
            {
                if (_pSerPosition?.GetValue(target) is PointF p) pos = p;
                if (_pSerSize?.GetValue(target) is SizeF s) size = s;
                if (_pSerOrigin?.GetValue(target) is PointF o) origin = o;
                object refObj = _pSerEntityRef?.GetValue(target);
                if (refObj != null)
                {
                    if (_pEntityRefId != null)
                        targetEntityId = Convert.ToUInt64(_pEntityRefId.GetValue(refObj));
                    else if (_fEntityRefId != null)
                        targetEntityId = Convert.ToUInt64(_fEntityRefId.GetValue(refObj));
                    else if (_pEntityRefEntity?.GetValue(refObj) is DXEntity refEnt)
                        targetEntityId = refEnt.ID;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] UnpackCommandTarget failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds a boxed ZXCommandTarget struct via reflection. Setting properties on the boxed
        /// value mutates the box in place, so the resulting object is a complete target.
        /// </summary>
        public static object CreateCommandTarget(PointF position, SizeF size, PointF origin, ulong targetEntityId)
        {
            EnsureCommandTargetMeta();
            if (_typeZXCommandTargetRef == null) return null;

            object box = Activator.CreateInstance(_typeZXCommandTargetRef);
            _pSerPosition?.SetValue(box, position);
            _pSerSize?.SetValue(box, size);
            _pSerOrigin?.SetValue(box, origin);

            if (targetEntityId != 0UL && _typeEntityRefClosed != null && _pEntityRefEntity != null && _pSerEntityRef != null)
            {
                var ent = DXGame.Current?.GetEntityByID(targetEntityId);
                if (ent != null && !ent.Disposed)
                {
                    if (TypeZXEntity == null || TypeZXEntity.IsInstanceOfType(ent))
                    {
                        object refBox = Activator.CreateInstance(_typeEntityRefClosed);
                        _pEntityRefEntity.SetValue(refBox, ent);
                        _pSerEntityRef.SetValue(box, refBox);
                    }
                    else
                    {
                        ModLogger.Warn($"[GameBridge] CreateCommandTarget ignored non-ZXEntity target id={targetEntityId} ({ent.GetType().Name}).");
                    }
                }
            }
            return box;
        }

        // ====================================================================
        // Mayor selection (ZX.ZXMayor). Identify the chosen mayor by its string ID and apply that
        // exact mayor on every peer — avoids re-running the male/female random pick per machine.
        // ====================================================================
        private static Type _typeZXMayor;
        private static FieldInfo _mayorRegistryField;
        private static PropertyInfo _mayorIdProp, _mayorNameProp, _mayorLevelProp, _mayorBonusEntitiesProp;
        private static MethodInfo _mayorApplyMethod;
        private static MethodInfo _levelStateIsChoosingMayorMethod;
        private static MethodInfo _levelStateSetChoosingMayorMethod;
        private static Type _typeZXWindow;
        private static MethodInfo _windowCloseMethod;

        private static void EnsureMayorMeta()
        {
            if (_typeZXMayor != null) return;
            _typeZXMayor = GameAssembly?.GetType("ZX.ZXMayor");
            if (_typeZXMayor != null)
            {
                const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                const BindingFlags IF = BindingFlags.Public | BindingFlags.Instance;
                _mayorRegistryField = _typeZXMayor.GetField(ObfuscatedSymbols.Field_ZXMayor_Registry, SF);
                _mayorIdProp = _typeZXMayor.GetProperty("ID", IF);
                _mayorNameProp = _typeZXMayor.GetProperty("Name", IF);
                _mayorLevelProp = _typeZXMayor.GetProperty("Level", IF);
                _mayorBonusEntitiesProp = _typeZXMayor.GetProperty("IDBonusEntities", IF);
                _mayorApplyMethod = AccessTools.Method(_typeZXMayor, ObfuscatedSymbols.Method_ZXMayor_Apply, new[] { LevelStateType });
                if (_mayorApplyMethod == null)
                {
                    foreach (var m in AccessTools.GetDeclaredMethods(_typeZXMayor))
                    {
                        if (m.Name == ObfuscatedSymbols.Method_ZXMayor_Apply && m.GetParameters().Length == 1)
                        { _mayorApplyMethod = m; break; }
                    }
                }
            }

            if (LevelStateType != null)
            {
                _levelStateIsChoosingMayorMethod = AccessTools.Method(LevelStateType, ObfuscatedSymbols.Method_ZXLevelState_IsChoosingMayor, Type.EmptyTypes);
                _levelStateSetChoosingMayorMethod = AccessTools.Method(LevelStateType, ObfuscatedSymbols.Method_ZXLevelState_SetChoosingMayor, new[] { typeof(bool) });
            }

            _typeZXWindow = GameAssembly?.GetType("ZX.GUI.ZXWindow");
            if (_typeZXWindow != null)
            {
                _windowCloseMethod = AccessTools.Method(_typeZXWindow, ObfuscatedSymbols.Method_ZXWindow_CloseWindow, new[] { typeof(Action) });
            }
        }

        public static List<Dictionary<string, object>> GetMayorList(int levelFilter)
        {
            EnsureMayorMeta();
            var result = new List<Dictionary<string, object>>();
            var dict = _mayorRegistryField?.GetValue(null) as IDictionary;
            if (dict == null) return result;
            foreach (DictionaryEntry kv in dict)
            {
                object mayor = kv.Value;
                if (mayor == null) continue;
                int lvl = _mayorLevelProp != null ? Convert.ToInt32(_mayorLevelProp.GetValue(mayor)) : 0;
                if (levelFilter > 0 && lvl != levelFilter) continue;
                result.Add(new Dictionary<string, object>
                {
                    ["id"] = _mayorIdProp != null ? Convert.ToString(_mayorIdProp.GetValue(mayor)) : Convert.ToString(kv.Key),
                    ["name"] = _mayorNameProp != null ? Convert.ToString(_mayorNameProp.GetValue(mayor)) : "?",
                    ["level"] = lvl,
                    ["bonusEntities"] = _mayorBonusEntitiesProp != null ? (Convert.ToString(_mayorBonusEntitiesProp.GetValue(mayor)) ?? string.Empty) : string.Empty
                });
            }
            return result;
        }

        public static int GetMayorLevel(object mayorObj)
        {
            EnsureMayorMeta();
            if (mayorObj == null || _mayorLevelProp == null) return 0;
            try { return Convert.ToInt32(_mayorLevelProp.GetValue(mayorObj)); }
            catch { return 0; }
        }

        public static string GetMayorNameById(string mayorId)
        {
            EnsureMayorMeta();
            if (string.IsNullOrEmpty(mayorId) || _mayorNameProp == null) return null;
            var dict = _mayorRegistryField?.GetValue(null) as IDictionary;
            if (dict == null || !dict.Contains(mayorId)) return null;
            try { return _mayorNameProp.GetValue(dict[mayorId]) as string; }
            catch { return null; }
        }

        public static bool IsChoosingMayor()
        {
            EnsureMayorMeta();
            object ls = CurrentLevelState;
            if (ls == null || _levelStateIsChoosingMayorMethod == null) return false;
            try { return (bool)_levelStateIsChoosingMayorMethod.Invoke(ls, null); }
            catch { return false; }
        }

        public static void SetChoosingMayor(bool choosing)
        {
            EnsureMayorMeta();
            object ls = CurrentLevelState;
            if (ls == null || _levelStateSetChoosingMayorMethod == null) return;
            try { _levelStateSetChoosingMayorMethod.Invoke(ls, new object[] { choosing }); }
            catch (Exception ex) { ModLogger.Warn($"[GameBridge] SetChoosingMayor({choosing}) failed: {ex.Message}"); }
        }

        public static bool IsZXWindow(object obj)
        {
            EnsureMayorMeta();
            return obj != null && _typeZXWindow != null && _typeZXWindow.IsInstanceOfType(obj);
        }

        public static bool CloseZXWindow(object window, Action onClosed = null)
        {
            EnsureMayorMeta();
            if (window == null || _windowCloseMethod == null) return false;
            try
            {
                _windowCloseMethod.Invoke(window, new object[] { onClosed });
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] CloseZXWindow failed: {ex.Message}");
                return false;
            }
        }

        // ====================================================================
        // Lobby Window & MainMenu Reuse Reflection Bridges (Iron Law #3 Safe)
        // ====================================================================
        private static Type _typeZXButtonSteam;
        private static Type _typeZXComboBox;
        private static Type _typeZXWindowTypeEnum;
        private static Type _typeZXWindowBtnPosEnum;
        private static Type _typeZXWindowAnimEnum;
        private static Type _typeZXFontCatalogFonts;
        private static FieldInfo _fieldMapLayersEnergyInitDone;
        private static FieldInfo _fieldMapLayersEnergyLock;
        private static PropertyInfo _propMapLayersNEnergyChanges;
        private static PropertyInfo _propMapLayersLayerEnergyUpdated;
        private static MethodInfo _methodMapLayersHasPathToAnyCC;
        private static MethodInfo _methodMapLayersHasPathBetween;
        private static MethodInfo _methodEnergyTransfererUpdateGrid;
        private static FieldInfo _fieldBuildingHasEnergyConn;
        private static MethodInfo _methodBuildingRefreshEnergy;
        private static MethodInfo _methodLevelSysIsCellPlayableForEnergy;
        private static PropertyInfo _windowContentProp;
        private static PropertyInfo _windowTitleProp;
        private static MethodInfo _windowShowMethod;
        private static MethodInfo _windowAddBottomBtnMethod;
        private static PropertyInfo _btnSteamTextProp;
        private static PropertyInfo _btnSteamOBarProp;
        private static PropertyInfo _btnSteamKeysProp;
        private static PropertyInfo _btnSteamSmallFontProp;
        private static FieldInfo _btnSteamLabelField;
        private static ConstructorInfo _btnSteamTextCtor;
        private static MethodInfo _methodReturnToMainMenu;
        private static MethodInfo _methodMainMenuLoadSave;
        private static MethodInfo _methodMainMenuNewSurvival;
        private static MethodInfo _methodMainMenuCustomMap;
        private static FieldInfo _fieldMainMenuContinueBtn;
        private static FieldInfo _fieldMainMenuReadyForInput;

        private static void EnsureLobbyUiMeta()
        {
            if (_typeZXButtonSteam != null) return;
            EnsureMayorMeta();

            _typeZXButtonSteam = GameAssembly?.GetType("ZX.GUI.ZXButtonSteam");
            _typeZXComboBox = GameAssembly?.GetType("ZX.GUI.ZXComboBox");
            if (_typeZXButtonSteam != null)
            {
                _btnSteamTextProp = AccessTools.Property(_typeZXButtonSteam, "Text");
                _btnSteamOBarProp = AccessTools.Property(_typeZXButtonSteam, "OBar");
                _btnSteamKeysProp = AccessTools.Property(_typeZXButtonSteam, "Keys");
                _btnSteamSmallFontProp = AccessTools.Property(_typeZXButtonSteam, "UseSmallFontStyle");
                _btnSteamLabelField = AccessTools.Field(_typeZXButtonSteam, "#=zMUT$V2s=");
                foreach (var c in _typeZXButtonSteam.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var ps = c.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string))
                    {
                        _btnSteamTextCtor = c;
                        break;
                    }
                }
            }

            if (_typeZXWindow != null)
            {
                _windowContentProp = AccessTools.Property(_typeZXWindow, "OContent");
                _windowTitleProp = AccessTools.Property(_typeZXWindow, "Title");
                _windowShowMethod = AccessTools.Method(_typeZXWindow, ObfuscatedSymbols.Method_ZXWindow_ShowWindow);
                _windowAddBottomBtnMethod = AccessTools.Method(_typeZXWindow, ObfuscatedSymbols.Method_ZXWindow_AddBottomButton);
                _typeZXWindowTypeEnum = _typeZXWindow.GetNestedType("TWindowType", BindingFlags.Public | BindingFlags.NonPublic);
                _typeZXWindowBtnPosEnum = _typeZXWindow.GetNestedType("TButtonPosition", BindingFlags.Public | BindingFlags.NonPublic);
                if (_windowShowMethod != null)
                {
                    var ps = _windowShowMethod.GetParameters();
                    if (ps.Length >= 2) _typeZXWindowAnimEnum = ps[1].ParameterType;
                }
            }

            if (TypeZXGame != null)
            {
                _methodReturnToMainMenu = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_ReturnToMainMenu, new[] { typeof(Action), typeof(bool) });
            }

            if (TypeZXSystemMainMenu != null)
            {
                _methodMainMenuLoadSave = AccessTools.Method(TypeZXSystemMainMenu, ObfuscatedSymbols.Method_MainMenu_OnContinueLoad, new[] { typeof(DXButton) });
                _methodMainMenuNewSurvival = AccessTools.Method(TypeZXSystemMainMenu, ObfuscatedSymbols.Method_MainMenu_OnNewSurvival, new[] { typeof(DXButton) });
                _methodMainMenuCustomMap = AccessTools.Method(TypeZXSystemMainMenu, ObfuscatedSymbols.Method_MainMenu_OnCustomMap, new[] { typeof(DXButton) });
                _fieldMainMenuContinueBtn = AccessTools.Field(TypeZXSystemMainMenu, ObfuscatedSymbols.Field_MainMenu_ContinueButton);
                _fieldMainMenuReadyForInput = AccessTools.Field(TypeZXSystemMainMenu, ObfuscatedSymbols.Field_MainMenu_ReadyForInput);
            }

            _typeZXFontCatalogFonts = GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXFontCatalogFonts);
        }

        public static bool IsOnMainMenu()
        {
            return MainMenuInstance != null;
        }

        public static bool IsAnyModalOpen()
        {
            try { return DXModalScreen.IsInModal; }
            catch { return false; }
        }

        public static bool HasAnySaveGames()
        {
            EnsureLobbyUiMeta();
            try
            {
                var menu = MainMenuInstance;
                if (menu != null && _fieldMainMenuContinueBtn != null)
                {
                    var btn = _fieldMainMenuContinueBtn.GetValue(menu) as DXButton;
                    if (btn != null) return btn.Enabled;
                }
                string savesDir = GetSavesDirectory();
                return !string.IsNullOrEmpty(savesDir) && System.IO.Directory.Exists(savesDir) && System.IO.Directory.GetFiles(savesDir, "*.zxsav").Length > 0;
            }
            catch { return false; }
        }

        public static bool OpenMainMenuLoadSave()
        {
            return InvokeMainMenuAction(_methodMainMenuLoadSave, "OpenMainMenuLoadSave");
        }

        public static bool OpenMainMenuNewSurvival()
        {
            return InvokeMainMenuAction(_methodMainMenuNewSurvival, "OpenMainMenuNewSurvival");
        }

        public static bool OpenMainMenuCustomMap()
        {
            return InvokeMainMenuAction(_methodMainMenuCustomMap, "OpenMainMenuCustomMap");
        }

        private static bool InvokeMainMenuAction(MethodInfo method, string label)
        {
            EnsureLobbyUiMeta();
            try
            {
                var menu = MainMenuInstance;
                if (menu == null || method == null)
                {
                    ModLogger.Warn($"[GameBridge] {label}: menu or method unavailable.");
                    return false;
                }
                if (_fieldMainMenuReadyForInput != null)
                    _fieldMainMenuReadyForInput.SetValue(menu, true);
                method.Invoke(menu, new object[] { null });
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[GameBridge] {label} failed", ex);
                return false;
            }
        }

        public static bool ReturnToMainMenu()
        {
            EnsureLobbyUiMeta();
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null || _methodReturnToMainMenu == null) return false;
                _methodReturnToMainMenu.Invoke(game, new object[] { null, true });
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] ReturnToMainMenu failed", ex);
                return false;
            }
        }

        /// <summary>Returns ZXLevelState.EndStatus as integer (0=Playing, 1=Won, 2=Lost).</summary>
        public static int GetLevelEndStatus()
        {
            try
            {
                object ls = CurrentLevelState;
                if (ls == null || _propLevelStateEndStatus == null) return 0;
                object val = _propLevelStateEndStatus.GetValue(ls);
                return val != null ? Convert.ToInt32(val) : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>True when either ZXLevelState.EndStatus != Playing or ZXSystemGameLevel.GameEndedFlag is set.</summary>
        public static bool IsLevelEnded()
        {
            if (CurrentLevelState == null) return false;
            if (GetLevelEndStatus() != 0) return true;
            try
            {
                object lvlSys = LevelSystemInstance;
                if (lvlSys != null && _fieldLevelSysGameEnded != null)
                {
                    object f = _fieldLevelSysGameEnded.GetValue(lvlSys);
                    if (f is bool b && b) return true;
                }
            }
            catch { }
            return false;
        }

        public static bool IsCurrentLevelSystemEnabled()
        {
            try
            {
                var sys = CurrentGameSystem as DXSystem;
                return sys != null && sys.Enabled;
            }
            catch
            {
                return false;
            }
        }

        public static void ClearScreenTransitionEffect()
        {
            try
            {
                _fieldLevelSysScreenAnim?.SetValue(null, null);
            }
            catch { }
        }

        public static bool TriggerGameEnd(bool won)
        {
            try
            {
                object lvlSys = LevelSystemInstance;
                if (lvlSys == null || _methodLevelSysTriggerGameEnd == null) return false;
                _methodLevelSysTriggerGameEnd.Invoke(lvlSys, new object[] { won });
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] TriggerGameEnd failed", ex);
                return false;
            }
        }

        public static bool ShowSyncedGameEnd(bool won)
        {
            try
            {
                object ls = CurrentLevelState;
                object lvlSys = LevelSystemInstance;
                if (ls == null || lvlSys == null) return false;
                if (IsLevelEnded()) return false;

                if (_propLevelStateEndStatus != null)
                {
                    object endVal = Enum.ToObject(_propLevelStateEndStatus.PropertyType, won ? 1 : 2);
                    _propLevelStateEndStatus.SetValue(ls, endVal);
                }
                _fieldLevelSysGameEnded?.SetValue(lvlSys, true);

                bool isCampaign = false;
                try
                {
                    var campaignCur = AccessTools.Method(typeof(ZXCampaign), ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes)?.Invoke(null, null);
                    isCampaign = campaignCur != null;
                }
                catch { }

                MethodInfo showMethod = isCampaign ? _methodLevelSysShowCampaignEnd : _methodLevelSysShowSurvivalEnd;
                if (showMethod == null) return false;
                showMethod.Invoke(lvlSys, new object[] { won, true });
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] ShowSyncedGameEnd failed", ex);
                return false;
            }
        }

        public static bool TriggerMayorPopup()
        {
            try
            {
                object game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null || CurrentLevelState == null || _methodGameShowMayorSelection == null) return false;
                SetChoosingMayor(true);
                _methodGameShowMayorSelection.Invoke(game, null);
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] TriggerMayorPopup failed", ex);
                return false;
            }
        }

        public static DXGUIObject CreateZXWindow(string title, bool bigWindow = true)
        {
            EnsureLobbyUiMeta();
            if (_typeZXWindow == null || _typeZXWindowTypeEnum == null) return null;
            object wtype = Enum.ToObject(_typeZXWindowTypeEnum, bigWindow ? 0 : 1);
            return Activator.CreateInstance(
                _typeZXWindow,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new object[] { title ?? string.Empty, wtype },
                null) as DXGUIObject;
        }

        public static DXObject GetZXWindowContent(DXGUIObject window)
        {
            EnsureLobbyUiMeta();
            return window != null ? _windowContentProp?.GetValue(window) as DXObject : null;
        }

        public static void SetZXWindowTitle(DXGUIObject window, string title)
        {
            EnsureLobbyUiMeta();
            if (window != null) _windowTitleProp?.SetValue(window, title ?? string.Empty);
        }

        public static DXButton AddZXWindowBottomButton(DXGUIObject window, int position, string text, DXKeys key = DXKeys.None, int hueRotation = 0)
        {
            EnsureLobbyUiMeta();
            if (window == null || _windowAddBottomBtnMethod == null || _typeZXWindowBtnPosEnum == null) return null;
            object posEnum = Enum.ToObject(_typeZXWindowBtnPosEnum, position);
            return _windowAddBottomBtnMethod.Invoke(window, new object[] { posEnum, text ?? string.Empty, null, key, hueRotation }) as DXButton;
        }

        public static void ShowZXWindow(DXGUIObject window, Action onShown = null, int animType = 2)
        {
            EnsureLobbyUiMeta();
            if (window == null || _windowShowMethod == null || _typeZXWindowAnimEnum == null) return;
            object anim = Enum.ToObject(_typeZXWindowAnimEnum, animType);
            _windowShowMethod.Invoke(window, new object[] { onShown, anim });
        }

        public static DXTextComboBox CreateZXComboBox(List<string> items, int selectedIndex = 0)
        {
            EnsureLobbyUiMeta();
            DXTextComboBox cb = null;
            if (_typeZXComboBox != null)
            {
                try
                {
                    cb = Activator.CreateInstance(
                        _typeZXComboBox,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null,
                        null,
                        null) as DXTextComboBox;
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[GameBridge] CreateZXComboBox reflection failed: {ex.Message}");
                }
            }
            if (cb == null)
                cb = new DXTextComboBox();
            cb.Items = items ?? new List<string>();
            if (cb.Items.Count > 0)
                cb.SelectedIndex = Math.Max(0, Math.Min(selectedIndex, cb.Items.Count - 1));
            return cb;
        }

        public static DXButton CreateZXButtonSteam(string text, DXKeys key = DXKeys.None, int hueRotation = 0, bool smallFont = false)
        {
            EnsureLobbyUiMeta();
            if (_typeZXButtonSteam == null || _btnSteamTextCtor == null) return null;
            var btn = _btnSteamTextCtor.Invoke(new object[] { text ?? string.Empty, null }) as DXButton;
            if (btn == null) return null;
            if (smallFont && _btnSteamSmallFontProp != null)
                _btnSteamSmallFontProp.SetValue(btn, true);
            if (key != DXKeys.None && _btnSteamKeysProp?.GetValue(btn) is IList<DXKeys> keys)
                keys.Add(key);
            if (hueRotation != 0)
                SetZXButtonSteamHue(btn, hueRotation);
            return btn;
        }

        public static void SetZXButtonSteamText(DXButton btn, string text)
        {
            EnsureLobbyUiMeta();
            if (btn == null || _btnSteamTextProp == null) return;
            string desired = text ?? string.Empty;
            string current = _btnSteamTextProp.GetValue(btn) as string;
            if (string.Equals(current, desired, StringComparison.Ordinal)) return;

            // Dispose and clear cached DXTextObject GPU texture before text mutation
            // so DXVision creates a fresh texture instead of ReLoadInGPU() (which skews on Direct3DEx).
            if (_btnSteamLabelField?.GetValue(btn) is DXObject labelObj && labelObj.Image != null)
            {
                try { labelObj.Image.Dispose(); } catch { }
                labelObj.Image = null;
            }
            _btnSteamTextProp.SetValue(btn, desired);
        }

        public static void SetZXButtonSteamHue(DXButton btn, int hueRotation)
        {
            EnsureLobbyUiMeta();
            if (btn == null || _btnSteamOBarProp == null) return;
            if (_btnSteamOBarProp.GetValue(btn) is DXObject obar)
                obar.HueRotation = hueRotation;
        }

        public static DXTextRenderer GetFontRenderer(string fontMethodName)
        {
            EnsureLobbyUiMeta();
            try
            {
                if (_typeZXFontCatalogFonts != null && !string.IsNullOrEmpty(fontMethodName))
                {
                    var m = AccessTools.Method(_typeZXFontCatalogFonts, fontMethodName, Type.EmptyTypes);
                    if (m?.Invoke(null, null) is DXTextRenderer tr) return tr;
                }
            }
            catch { }
            return GetPlainTextRenderer();
        }

        public static bool ApplyMayor(string mayorId)
        {
            EnsureMayorMeta();
            if (string.IsNullOrEmpty(mayorId) || _mayorApplyMethod == null) return false;
            var dict = _mayorRegistryField?.GetValue(null) as IDictionary;
            if (dict == null || !dict.Contains(mayorId)) return false;
            object mayor = dict[mayorId];
            object ls = CurrentLevelState;
            if (mayor == null || ls == null) return false;

            int mayorLevel = GetMayorLevel(mayor);
            int currentCount = GetLevelCollectionCount("MayorStates");
            if (mayorLevel > 0 && currentCount >= mayorLevel)
            {
                ModLogger.Info($"[GameBridge] Ignored duplicate mayor selection '{mayorId}' (level={mayorLevel}, alreadySelected={currentCount}).");
                SetChoosingMayor(false);
                Patches.MayorSyncPatch.CloseActiveMayorWindow();
                return false;
            }

            try
            {
                _mayorApplyMethod.Invoke(mayor, new[] { ls });
                SetChoosingMayor(false);
                Patches.MayorSyncPatch.CloseActiveMayorWindow();
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] ApplyMayor failed: {ex.Message}");
                return false;
            }
        }

        public static int GetLevelCollectionCount(string name)
        {
            var prop = GetLevelProperty(name);
            var ls = CurrentLevelState;
            if (prop == null || ls == null) return 0;
            try
            {
                object v = prop.GetValue(ls);
                if (v is ICollection col) return col.Count;
                if (v is IEnumerable en) { int n = 0; foreach (var _ in en) n++; return n; }
            }
            catch { }
            return 0;
        }

        // ====================================================================
        // Right-click interaction, CCommandable snapshot & unit wake-up helpers
        // ====================================================================

        public struct CommandableSnapshot
        {
            public object Commandable;
            public object Cmd;
            public object Target;
        }

        private static bool _interactionMetaInit;
        private static FieldInfo _fLevelPendingCmd;
        private static FieldInfo _fLevelMouseEntity;
        private static MethodInfo _mIsOppositeNature;
        private static PropertyInfo _pCapsuleBroken;
        private static FieldInfo _fCmdableCurCmd;
        private static FieldInfo _fCmdableCurTarget;
        private static MethodInfo _mBehavSetResetState;
        private static MethodInfo _mBehavSetResetMem;
        private static MethodInfo _mBehavTick;
        private static MethodInfo _mMovableTick;

        private static void EnsureInteractionMeta()
        {
            if (_interactionMetaInit) return;
            _interactionMetaInit = true;
            try
            {
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _fLevelPendingCmd = TypeZXSystemGameLevel?.GetField(ObfuscatedSymbols.Field_LevelSys_PendingCommand, F);
                _fLevelMouseEntity = TypeZXSystemGameLevel?.GetField(ObfuscatedSymbols.Field_LevelSys_MouseEntity, F);

                Type zxEntity = TypeZXEntity;
                if (zxEntity != null)
                    _mIsOppositeNature = AccessTools.Method(zxEntity, "#=zyqIcAEeeAjQu", new[] { zxEntity });

                Type tCapsule = GameAssembly?.GetType("ZX.Entities.InfectedCapsule");
                if (tCapsule != null)
                    _pCapsuleBroken = tCapsule.GetProperty("Broken", BindingFlags.Public | BindingFlags.Instance);

                Type tCmdable = TypeCCommandable;
                if (tCmdable != null)
                {
                    _fCmdableCurCmd = tCmdable.GetField("#=z8dbvOmM=", F);
                    _fCmdableCurTarget = tCmdable.GetField("#=zyCiF7Lo=", F);
                }

                Type tCBehav = _mGetEntityBehaviour?.ReturnType ?? GameAssembly?.GetType("ZX.Components.CBehaviour");
                if (tCBehav != null)
                {
                    _mBehavSetResetState = AccessTools.Method(tCBehav, "#=zhPQK_m4yjl9wB_DGFw==", new[] { typeof(bool) });
                    _mBehavSetResetMem = AccessTools.Method(tCBehav, "#=zXeRMQqKCGSFidmsp4w==", new[] { typeof(bool) });
                    _mBehavTick = AccessTools.Method(tCBehav, "#=z5GPRaBw=", Type.EmptyTypes);
                }

                Type tCMov = TypeCMovable;
                if (tCMov != null)
                    _mMovableTick = AccessTools.Method(tCMov, "#=z5GPRaBw=", Type.EmptyTypes);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] EnsureInteractionMeta failed: {ex.Message}");
            }
        }

        public static object GetPendingCommandOnLevelSys()
        {
            EnsureInteractionMeta();
            object levelSys = LevelSystemInstance;
            if (levelSys == null || _fLevelPendingCmd == null) return null;
            try { return _fLevelPendingCmd.GetValue(levelSys); } catch { return null; }
        }

        public static DXEntity GetMouseEntityOnLevelSys()
        {
            EnsureInteractionMeta();
            object levelSys = LevelSystemInstance;
            if (levelSys == null || _fLevelMouseEntity == null) return null;
            try { return _fLevelMouseEntity.GetValue(levelSys) as DXEntity; } catch { return null; }
        }

        public static object GetAttackCommandObject() => GetCommandByName("Attack");

        public static bool IsEnemyOrAttackableCapsule(DXEntity targetEntity, DXEntity referencePlayerUnit)
        {
            if (targetEntity == null || targetEntity.Disposed || referencePlayerUnit == null)
                return false;
            EnsureInteractionMeta();
            try
            {
                if (_mIsOppositeNature != null && (bool)_mIsOppositeNature.Invoke(targetEntity, new object[] { referencePlayerUnit }))
                    return true;
                if (string.Equals(targetEntity.GetType().Name, "InfectedCapsule", StringComparison.Ordinal))
                    return _pCapsuleBroken == null || !(bool)_pCapsuleBroken.GetValue(targetEntity);
            }
            catch { }
            return false;
        }

        public static List<CommandableSnapshot> SnapshotSelectedCommandables()
        {
            EnsureInteractionMeta();
            if (_fCmdableCurCmd == null || _fCmdableCurTarget == null) return null;

            List<CommandableSnapshot> snaps = null;
            foreach (object item in EnumerateSelectedComponents())
            {
                var ent = (item as DXComponent)?.Entity;
                if (ent == null || ent.Disposed || !IsPlayerEntity(ent)) continue;
                object cmdable = GetCommandableObject(ent);
                if (cmdable == null) continue;
                if (snaps == null) snaps = new List<CommandableSnapshot>();
                snaps.Add(new CommandableSnapshot
                {
                    Commandable = cmdable,
                    Cmd = _fCmdableCurCmd.GetValue(cmdable),
                    Target = _fCmdableCurTarget.GetValue(cmdable)
                });
            }
            return snaps;
        }

        public static void RestoreSelectedCommandables(List<CommandableSnapshot> snaps)
        {
            if (snaps == null || _fCmdableCurCmd == null || _fCmdableCurTarget == null) return;
            for (int i = 0; i < snaps.Count; i++)
            {
                var s = snaps[i];
                if (s.Commandable == null) continue;
                try
                {
                    _fCmdableCurCmd.SetValue(s.Commandable, s.Cmd);
                    _fCmdableCurTarget.SetValue(s.Commandable, s.Target);
                }
                catch { }
            }
        }

        public static bool IsWeaponSubCommand(object cmdObj)
        {
            if (cmdObj == null) return false;
            string name = cmdObj.GetType().Name;
            if (string.Equals(name, "Attack", StringComparison.Ordinal))
                return false;
            return name.EndsWith("Attack", StringComparison.Ordinal)
                || string.Equals(name, "ArtilleryManExplosive", StringComparison.Ordinal);
        }

        public struct PendingResourceCost
        {
            public int Gold;
            public int Wood;
            public int Stone;
            public int Iron;
            public int Oil;
            public int Energy;
            public int Food;
            public int Workers;

            public static PendingResourceCost operator +(PendingResourceCost a, PendingResourceCost b)
            {
                return new PendingResourceCost
                {
                    Gold = a.Gold + b.Gold,
                    Wood = a.Wood + b.Wood,
                    Stone = a.Stone + b.Stone,
                    Iron = a.Iron + b.Iron,
                    Oil = a.Oil + b.Oil,
                    Energy = a.Energy + b.Energy,
                    Food = a.Food + b.Food,
                    Workers = a.Workers + b.Workers
                };
            }
        }

        private static bool _costPropsInit;
        private static PropertyInfo _propCostGold, _propCostWood, _propCostStone, _propCostIron, _propCostOil, _propCostEnergy, _propCostFood, _propCostWorkers;

        public static bool TryGetCommandResourceCost(object command, object executor, out PendingResourceCost cost)
        {
            cost = default;
            if (command == null || executor == null || _methodGetResourcesCost == null)
                return false;

            try
            {
                object boxedCost = _methodGetResourcesCost.Invoke(command, new[] { executor });
                if (boxedCost == null)
                    return false;

                if (!_costPropsInit)
                {
                    Type tCost = boxedCost.GetType();
                    _propCostGold = tCost.GetProperty("Gold", BindingFlags.Public | BindingFlags.Instance);
                    _propCostWood = tCost.GetProperty("Wood", BindingFlags.Public | BindingFlags.Instance);
                    _propCostStone = tCost.GetProperty("Stone", BindingFlags.Public | BindingFlags.Instance);
                    _propCostIron = tCost.GetProperty("Iron", BindingFlags.Public | BindingFlags.Instance);
                    _propCostOil = tCost.GetProperty("Oil", BindingFlags.Public | BindingFlags.Instance);
                    _propCostEnergy = tCost.GetProperty("Energy", BindingFlags.Public | BindingFlags.Instance);
                    _propCostFood = tCost.GetProperty("Food", BindingFlags.Public | BindingFlags.Instance);
                    _propCostWorkers = tCost.GetProperty("Workers", BindingFlags.Public | BindingFlags.Instance);
                    _costPropsInit = true;
                }

                int ReadPosInt(PropertyInfo p) => p != null ? Math.Max(0, Convert.ToInt32(p.GetValue(boxedCost, null))) : 0;
                cost = new PendingResourceCost
                {
                    Gold = ReadPosInt(_propCostGold),
                    Wood = ReadPosInt(_propCostWood),
                    Stone = ReadPosInt(_propCostStone),
                    Iron = ReadPosInt(_propCostIron),
                    Oil = ReadPosInt(_propCostOil),
                    Energy = ReadPosInt(_propCostEnergy),
                    Food = ReadPosInt(_propCostFood),
                    Workers = ReadPosInt(_propCostWorkers)
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool CanAffordWithPending(PendingResourceCost candidate, PendingResourceCost pendingTotal)
        {
            object ls = CurrentLevelState;
            if (ls == null) return true;

            PendingResourceCost total = candidate + pendingTotal;
            try
            {
                if (total.Gold > 0 && GetLevelInt("Gold") < total.Gold) return false;
                if (total.Wood > 0 && GetLevelInt("Wood") < total.Wood) return false;
                if (total.Stone > 0 && GetLevelInt("Stone") < total.Stone) return false;
                if (total.Iron > 0 && GetLevelInt("Iron") < total.Iron) return false;
                if (total.Oil > 0 && GetLevelInt("Oil") < total.Oil) return false;

                if (total.Energy > 0 && _methodLevelStateAvailEnergy != null
                    && Convert.ToInt32(_methodLevelStateAvailEnergy.Invoke(ls, null)) < total.Energy)
                    return false;
                if (total.Food > 0 && _methodLevelStateAvailFood != null
                    && Convert.ToInt32(_methodLevelStateAvailFood.Invoke(ls, null)) < total.Food)
                    return false;
                if (total.Workers > 0 && _methodLevelStateAvailWorkers != null
                    && Convert.ToInt32(_methodLevelStateAvailWorkers.Invoke(ls, null)) < total.Workers)
                    return false;

                return true;
            }
            catch
            {
                return true;
            }
        }

        public static bool IsAttackCommand(object cmdObj)
        {
            if (cmdObj == null) return false;
            string name = cmdObj.GetType().Name;
            return name.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(name, "UseItem_Grenade", StringComparison.Ordinal);
        }

        public static void PlayUnitCommandVoice(DXEntity entity, bool isAttack)
        {
            if (entity == null || entity.Disposed || DXScene.Current == null || _methodEntityGetDefaultParams == null)
                return;
            if (TypeZXEntity != null && !TypeZXEntity.IsInstanceOfType(entity))
                return;

            Determinism.LockstepClock.EnterAudioContext();
            try
            {
                object audio = _fieldAudioCurrent?.GetValue(null);
                if (audio == null) return;

                object defParams = _methodEntityGetDefaultParams.Invoke(entity, null);
                if (defParams == null) return;

                if (_propDefParamsSoundsGeneric == null || _propDefParamsSoundsAttack == null)
                {
                    Type dpType = defParams.GetType();
                    _propDefParamsSoundsGeneric = dpType.GetProperty("SoundsOnCommandGeneric", BindingFlags.Public | BindingFlags.Instance);
                    _propDefParamsSoundsAttack = dpType.GetProperty("SoundsOnCommandAttack", BindingFlags.Public | BindingFlags.Instance);
                }

                var list = (isAttack
                    ? _propDefParamsSoundsAttack?.GetValue(defParams, null) as List<string>
                    : _propDefParamsSoundsGeneric?.GetValue(defParams, null) as List<string>);

                if ((list == null || list.Count == 0) && isAttack)
                    list = _propDefParamsSoundsGeneric?.GetValue(defParams, null) as List<string>;

                if (list == null || list.Count == 0) return;

                if (!isAttack && _typeUnit != null && _typeUnit.IsInstanceOfType(entity) && _methodAudioPickWeightedVoice != null)
                {
                    _methodAudioPickWeightedVoice.Invoke(audio, new object[] { list });
                }
                else if (_methodAudioPlayUniqueList != null)
                {
                    _methodAudioPlayUniqueList.Invoke(audio, new object[] { ZXSoundCategory.UnitVoices, ZXUniqueSoundType.EntityVoice, list });
                }
            }
            catch { }
            finally
            {
                Determinism.LockstepClock.ExitAudioContext();
            }
        }

        public static void PlayLocalBuildOrOrderFeedback(Type targetEntityType, bool preferCreationSound, DXEntity fallbackExecutor = null)
        {
            if (DXScene.Current == null || _methodAudioPlay2DSoundList == null || _methodEntityGetDefaultParams == null)
                return;

            Determinism.LockstepClock.EnterAudioContext();
            try
            {
                object audio = _fieldAudioCurrent?.GetValue(null);
                if (audio == null) return;

                DXEntity sampleEntity = targetEntityType != null ? GetEntityTemplateByType(targetEntityType)?.Entity : null;
                if (sampleEntity == null) sampleEntity = fallbackExecutor;
                if (sampleEntity == null) return;

                object defParams = _methodEntityGetDefaultParams.Invoke(sampleEntity, null);
                if (defParams == null) return;

                if (_propDefParamsSoundsCreation == null || _propDefParamsSoundsSelected == null)
                {
                    Type dpType = defParams.GetType();
                    _propDefParamsSoundsCreation = dpType.GetProperty("SoundsOnCreation", BindingFlags.Public | BindingFlags.Instance);
                    _propDefParamsSoundsSelected = dpType.GetProperty("SoundsOnSelected", BindingFlags.Public | BindingFlags.Instance);
                }

                var list = (preferCreationSound
                    ? _propDefParamsSoundsCreation?.GetValue(defParams, null) as List<string>
                    : _propDefParamsSoundsSelected?.GetValue(defParams, null) as List<string>);
                if ((list == null || list.Count == 0))
                    list = _propDefParamsSoundsSelected?.GetValue(defParams, null) as List<string>;

                if (list != null && list.Count > 0)
                {
                    _methodAudioPlay2DSoundList.Invoke(audio, new object[] { ZXSoundCategory.Effects, list });
                }
            }
            catch { }
            finally
            {
                Determinism.LockstepClock.ExitAudioContext();
            }
        }

        public static void NotifyUnitCommandExecuted(object executorObj, object cmdObj)
        {
            if (IsWeaponSubCommand(cmdObj)) return;
            var ent = executorObj as DXEntity;
            if (ent == null || ent.Disposed) return;
            EnsureInteractionMeta();

            object cbehav = null;
            if (_mGetEntityBehaviour != null && _mGetEntityBehaviour.DeclaringType.IsInstanceOfType(ent))
            {
                try { cbehav = _mGetEntityBehaviour.Invoke(ent, null); } catch { cbehav = null; }
            }

            if (cbehav != null)
            {
                try
                {
                    _mBehavSetResetMem?.Invoke(cbehav, new object[] { true });
                    string cmdName = cmdObj?.GetType().Name;
                    if (string.Equals(cmdName, "Interact", StringComparison.Ordinal)
                        || string.Equals(cmdName, "Travel", StringComparison.Ordinal))
                    {
                        _mBehavSetResetState?.Invoke(cbehav, new object[] { true });
                    }
                }
                catch { }
            }

            // Wake odd-parity units on the exact command tick so an entire squad reacts together
            // without waiting up to 33-100ms staggered across IsIAFrameModule(4) parity boundaries.
            if (Determinism.LockstepClock.IsDeterministicModeActive
                && !Determinism.LockstepClock.PausedBySession
                && !ent.IsIAFrameModule(4))
            {
                int origHash = ent.HashCode;
                ent.HashCode = origHash ^ 1;
                try
                {
                    if (cbehav != null) _mBehavTick?.Invoke(cbehav, null);
                    if (_mGetEntityMovable != null && _mGetEntityMovable.DeclaringType.IsInstanceOfType(ent))
                    {
                        object cmov = _mGetEntityMovable.Invoke(ent, null);
                        if (cmov != null) _mMovableTick?.Invoke(cmov, null);
                    }
                }
                catch { }
                finally
                {
                    ent.HashCode = origHash;
                }
            }
        }

        // ====================================================================
        // Random Survival Map Generator & Balance Scaling Helpers
        // ====================================================================

        public static MethodInfo GetMapGeneratorMethod() => _methodMapGeneratorGenerate;

        public static int GetRandomParamsNCells(object randomParams)
        {
            if (randomParams == null || _propRandomParamsNCells == null) return 256;
            return (int)_propRandomParamsNCells.GetValue(randomParams);
        }

        public static void SetRandomParamsNCells(object randomParams, int nCells)
        {
            if (randomParams != null && _propRandomParamsNCells != null)
                _propRandomParamsNCells.SetValue(randomParams, nCells);
        }

        public static float GetRandomParamsFactorGameDuration(object randomParams)
        {
            if (randomParams == null || _propRandomParamsFactorDuration == null) return 1f;
            return (float)_propRandomParamsFactorDuration.GetValue(randomParams);
        }

        public static IDictionary GetMapThemesDictionary()
        {
            if (_methodZXMapThemeGetThemes == null) return null;
            return _methodZXMapThemeGetThemes.Invoke(null, null) as IDictionary;
        }

        public struct MapThemeBalanceSnapshot
        {
            public object ThemeObj;
            public DXRange<int> NumDoomVillages;
            public DXRange<int> DoomVillagesSize;
            public DXRange<int> NumTreasures;
            public DXRange<int> NumSuppyFood;
            public DXRange<int> NumSuppyEnergy;
            public int NumInfectedGiants;
            public int NumInfectedMutants;
            public double InfectedFactor;
        }

        public static MapThemeBalanceSnapshot CaptureMapThemeSnapshot(object themeObj)
        {
            return new MapThemeBalanceSnapshot
            {
                ThemeObj = themeObj,
                NumDoomVillages = _propMapThemeNumDoomVillages != null ? (DXRange<int>)_propMapThemeNumDoomVillages.GetValue(themeObj) : default(DXRange<int>),
                DoomVillagesSize = (DXRange<int>)_propMapThemeDoomVillagesSize.GetValue(themeObj),
                NumTreasures = (DXRange<int>)_propMapThemeNumTreasures.GetValue(themeObj),
                NumSuppyFood = (DXRange<int>)_propMapThemeNumSuppyFood.GetValue(themeObj),
                NumSuppyEnergy = (DXRange<int>)_propMapThemeNumSuppyEnergy.GetValue(themeObj),
                NumInfectedGiants = (int)_propMapThemeNumInfectedGiants.GetValue(themeObj),
                NumInfectedMutants = (int)_propMapThemeNumInfectedMutants.GetValue(themeObj),
                InfectedFactor = (double)_propMapThemeInfectedFactor.GetValue(themeObj)
            };
        }

        public static void ApplyMapThemeValues(
            object themeObj,
            DXRange<int> doomVillagesSize,
            DXRange<int> numTreasures,
            DXRange<int> numSuppyFood,
            DXRange<int> numSuppyEnergy,
            int numInfectedGiants,
            int numInfectedMutants,
            double infectedFactor)
        {
            if (themeObj == null) return;
            _propMapThemeDoomVillagesSize.SetValue(themeObj, doomVillagesSize);
            _propMapThemeNumTreasures.SetValue(themeObj, numTreasures);
            _propMapThemeNumSuppyFood.SetValue(themeObj, numSuppyFood);
            _propMapThemeNumSuppyEnergy.SetValue(themeObj, numSuppyEnergy);
            _propMapThemeNumInfectedGiants.SetValue(themeObj, numInfectedGiants);
            _propMapThemeNumInfectedMutants.SetValue(themeObj, numInfectedMutants);
            _propMapThemeInfectedFactor.SetValue(themeObj, infectedFactor);
        }

        public static void RestoreMapThemeSnapshot(MapThemeBalanceSnapshot snap)
        {
            if (snap.ThemeObj != null && _propMapThemeNumDoomVillages != null)
                _propMapThemeNumDoomVillages.SetValue(snap.ThemeObj, snap.NumDoomVillages);
            ApplyMapThemeValues(
                snap.ThemeObj,
                snap.DoomVillagesSize,
                snap.NumTreasures,
                snap.NumSuppyFood,
                snap.NumSuppyEnergy,
                snap.NumInfectedGiants,
                snap.NumInfectedMutants,
                snap.InfectedFactor);
        }

        public static bool GetMapThemeZombieProfile(
            object randomParams,
            out double infectedFactor,
            out double infectedDispersion,
            out int minWeak,
            out int minMed,
            out int minStrong,
            out int minPowerful)
        {
            infectedFactor = 0.45;
            infectedDispersion = 0.04;
            minWeak = 28;
            minMed = 44;
            minStrong = 62;
            minPowerful = 75;
            try
            {
                if (randomParams == null || _propRandomParamsThemeType == null) return false;
                object themeKey = _propRandomParamsThemeType.GetValue(randomParams);
                IDictionary themes = GetMapThemesDictionary();
                if (themeKey == null || themes == null || !themes.Contains(themeKey)) return false;
                object themeObj = themes[themeKey];
                if (themeObj == null) return false;

                if (_propMapThemeInfectedFactor != null)
                    infectedFactor = (double)_propMapThemeInfectedFactor.GetValue(themeObj);
                if (_propMapThemeInfectedDispersion != null)
                    infectedDispersion = Convert.ToDouble(_propMapThemeInfectedDispersion.GetValue(themeObj));
                if (_propMapThemeMinDistWeak != null)
                    minWeak = (int)_propMapThemeMinDistWeak.GetValue(themeObj);
                if (_propMapThemeMinDistMed != null)
                    minMed = (int)_propMapThemeMinDistMed.GetValue(themeObj);
                if (_propMapThemeMinDistStrong != null)
                    minStrong = (int)_propMapThemeMinDistStrong.GetValue(themeObj);
                if (_propMapThemeMinDistPowerful != null)
                    minPowerful = (int)_propMapThemeMinDistPowerful.GetValue(themeObj);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetGeneratedLevelContext(
            DXLevel level,
            out object mapDrawer,
            out List<DXEntity> extraEntities,
            out DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            out DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            out int gridNCells,
            out IList levelEvents)
        {
            mapDrawer = null;
            extraEntities = null;
            layerTerrain = null;
            layerObjects = null;
            gridNCells = 0;
            levelEvents = null;

            object ext = level?.Extension;
            if (ext == null || (_typeZXLevelExtension != null && !_typeZXLevelExtension.IsInstanceOfType(ext)))
                return false;

            mapDrawer = _propLevelExtMapDrawer?.GetValue(ext);
            levelEvents = _propLevelExtLevelEvents?.GetValue(ext) as IList;
            if (mapDrawer == null) return false;

            extraEntities = _propMapDrawerExtraEntities?.GetValue(mapDrawer) as List<DXEntity>;
            layerTerrain = _propMapDrawerLayerTerrain?.GetValue(mapDrawer) as DXGridLayerEnum<ZXMapLayerTerrainType>;
            layerObjects = _propMapDrawerLayerObjects?.GetValue(mapDrawer) as DXGridLayerEnum<ZXMapLayerObjectType>;
            gridNCells = _propMapDrawerNCells != null ? (int)_propMapDrawerNCells.GetValue(mapDrawer) : level.NCells;
            return extraEntities != null;
        }

        public static void ScaleLevelEventPopulation(object levelEvent, float factor, int maxPower)
        {
            if (levelEvent == null || _methodLevelEventScalePop == null) return;
            _methodLevelEventScalePop.Invoke(levelEvent, new object[] { factor, maxPower });
        }

        public static void GetLevelEventTiming(object levelEvent, out int startTimeH, out int gameTimeH, out bool isFinalSwarm)
        {
            startTimeH = _propLevelEventStartTimeH != null ? (int)_propLevelEventStartTimeH.GetValue(levelEvent) : 0;
            gameTimeH = _propLevelEventGameTimeH != null ? (int)_propLevelEventGameTimeH.GetValue(levelEvent) : 0;
            isFinalSwarm = _propLevelEventFinalSwarm != null && (bool)_propLevelEventFinalSwarm.GetValue(levelEvent);
        }

        public static DXEntityTemplate GetEntityTemplateByType(Type entityType)
        {
            if (entityType == null || _methodGetTemplateFromEntityType == null) return null;
            DXGame zxGame = ZXGameInstance;
            if (zxGame == null) return null;
            return _methodGetTemplateFromEntityType.Invoke(zxGame, new object[] { entityType }) as DXEntityTemplate;
        }

        public static bool AddLevelStateCommandCenterExtras(int addWorkers, int addFood, int addEnergy = 0)
        {
            object ls = CurrentLevelState;
            if (ls == null || _propLevelStateExtraWorkers == null || _propLevelStateExtraFood == null) return false;

            if (addWorkers != 0)
            {
                int curW = (int)_propLevelStateExtraWorkers.GetValue(ls);
                _propLevelStateExtraWorkers.SetValue(ls, curW + addWorkers);
            }
            if (addFood != 0)
            {
                int curF = (int)_propLevelStateExtraFood.GetValue(ls);
                _propLevelStateExtraFood.SetValue(ls, curF + addFood);
            }
            if (addEnergy != 0 && _pLsExtraEnergy != null)
            {
                int curE = (int)_pLsExtraEnergy.GetValue(ls);
                _pLsExtraEnergy.SetValue(ls, curE + addEnergy);
            }
            return true;
        }

        public static void RecalcLevelStateResources()
        {
            object ls = CurrentLevelState;
            if (ls == null || _methodLevelStateRecalcResources == null) return;
            try { _methodLevelStateRecalcResources.Invoke(ls, null); } catch { }
        }

        public static void GetLevelStateCommandCenterExtras(out int extraWorkers, out int extraFood)
        {
            extraWorkers = 0;
            extraFood = 0;
            object ls = CurrentLevelState;
            if (ls == null) return;
            if (_propLevelStateExtraWorkers != null) extraWorkers = (int)_propLevelStateExtraWorkers.GetValue(ls);
            if (_propLevelStateExtraFood != null) extraFood = (int)_propLevelStateExtraFood.GetValue(ls);
        }

        public static bool CaptureMapPanorama(string outputPath, int resolution = 2048, bool revealFog = true)
        {
            var lvlSys = LevelSystemInstance;
            if (lvlSys == null || _methodLevelSysCapturePanorama == null || DXLevel.Current == null)
                return false;

            FieldInfo progressWinField = null;
            object prevProgressWin = null;
            object tempProgressWin = null;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

                ClearLoadingFadeOverlay();
                ClearWaitCursor();

                // Cancel the 6-second New Game intro blur/cinematic so Paused frames are crisp.
                AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_IntroCinematic)?.SetValue(null, false);
                AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_ScreenAnim)?.SetValue(null, null);
                var rootObj = DXScene.Current?.RootObject;
                if (rootObj != null)
                {
                    rootObj.Blur = 0f;
                    if (rootObj.State != null) rootObj.State.Blur = 0f;
                }

                if (revealFog)
                {
                    RevealAll(true);
                    object ml = GetMapLayersInstance();
                    if (ml != null)
                    {
                        SetInstanceBool(ml, "ShadowEnabled", false);
                        AccessTools.Method(ml.GetType(), "OnFinishUpdate")?.Invoke(ml, null);
                    }
                    if (DXScene.Current != null && DXLevel.Current != null)
                    {
                        DXScene.Current.GlobalLightColor = DXLevel.Current.GlobalLightColor;
                    }
                }

                var helperType = GameAssembly?.GetType(ObfuscatedSymbols.Type_ProgressWindowHelper);
                progressWinField = helperType != null
                    ? AccessTools.Field(helperType, ObfuscatedSymbols.Field_ProgressWindow_Current)
                    : null;
                if (progressWinField != null)
                {
                    prevProgressWin = progressWinField.GetValue(null);
                    if (prevProgressWin == null)
                    {
                        var winType = GameAssembly?.GetType("ZX.GUI.ZXWindow");
                        var ctors = winType?.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (ctors != null && ctors.Length > 0)
                        {
                            var ctor = ctors[0];
                            var ps = ctor.GetParameters();
                            var cArgs = new object[ps.Length];
                            for (int i = 0; i < ps.Length; i++)
                            {
                                var pt = ps[i].ParameterType;
                                if (pt == typeof(string)) cArgs[i] = "";
                                else if (pt.IsEnum) cArgs[i] = Enum.ToObject(pt, 0);
                                else if (ps[i].HasDefaultValue) cArgs[i] = ps[i].DefaultValue;
                                else if (pt.IsValueType) cArgs[i] = Activator.CreateInstance(pt);
                            }
                            tempProgressWin = ctor.Invoke(cArgs);
                            if (tempProgressWin is DXObject dxObj) dxObj.Visible = false;
                            progressWinField.SetValue(null, tempProgressWin);
                        }
                    }
                }

                _methodLevelSysCapturePanorama.Invoke(lvlSys, new object[] { outputPath, resolution });
                return System.IO.File.Exists(outputPath) || System.IO.File.Exists(System.IO.Path.ChangeExtension(outputPath, ".png"));
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[GameBridge] CaptureMapPanorama({outputPath}) failed", ex);
                return false;
            }
            finally
            {
                if (progressWinField != null && tempProgressWin != null)
                {
                    try { progressWinField.SetValue(null, prevProgressWin); } catch { }
                    try { (tempProgressWin as IDisposable)?.Dispose(); } catch { }
                }
            }
        }

        public static volatile bool SurvivalGenInProgress;
        public static volatile bool SurvivalGenLastOk;
        public static volatile string SurvivalGenLastError;

        public static bool StartSurvivalAsync(
            int seed,
            string themeName,
            float factorZombiePop = 1f,
            float factorDuration = 1f,
            Sync.CoopBalancePreset? overridePreset = null,
            int? overridePlayerCount = null,
            bool? overrideMultiFaction = null,
            Sync.MultiFactionSpawnMode? overrideSpawnMode = null)
        {
            if (SurvivalGenInProgress)
            {
                ModLogger.Warn("[GameBridge] StartSurvivalAsync: generation already in progress.");
                return false;
            }

            try
            {
                if (TypeZXGame == null || TypeZXSystemGameLevel == null || _typeZXGameState == null ||
                    _typeZXRandomLevelParams == null || _methodMapGeneratorGenerate == null || _mInitLevel == null)
                {
                    SurvivalGenLastError = "missing required bindings";
                    ModLogger.Warn("[GameBridge] StartSurvivalAsync: missing required bindings.");
                    return false;
                }

                SurvivalGenInProgress = true;
                SurvivalGenLastOk = false;
                SurvivalGenLastError = null;

                if (DXModalScreen.IsInModal && DXModalScreen.CurrentModalObject != null)
                {
                    DXModalScreen.RemoveModal(DXModalScreen.CurrentModalObject);
                }
                PrepareForProgrammaticLoad();

                var game = ZXGameInstance;
                var asyncTransitionMethod = AccessTools.Method(
                    TypeZXGame,
                    ObfuscatedSymbols.Method_ZXGame_AsyncTransition,
                    new[] { typeof(int), typeof(Action), typeof(Action), typeof(List<string>), typeof(bool) });
                if (asyncTransitionMethod == null)
                {
                    SurvivalGenLastError = "missing ZXGame.AsyncTransition method";
                    SurvivalGenInProgress = false;
                    return false;
                }

                if (!Enum.TryParse<ZXMapThemeType>(themeName ?? "BR", true, out var themeType))
                    themeType = ZXMapThemeType.BR;

                var resetMethod = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_ResetWorldState, Type.EmptyTypes);
                var lsType = LevelStateType ?? GameAssembly.GetType("ZX.ZXLevelState");

                MethodInfo loadGeneric = null;
                foreach (var m in typeof(DXSystem).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "Load" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                    {
                        loadGeneric = m.MakeGenericMethod(TypeZXSystemGameLevel);
                        break;
                    }
                }

                object newSys = null;
                DXLevel generated = null;
                string workerErr = null;

                Action workerAction = () =>
                {
                    try
                    {
                        ModLogger.Info($"[GameBridge] StartSurvivalAsync worker begin: theme={themeType}, seed={seed}, preset={overridePreset}, players={overridePlayerCount}");
                        resetMethod?.Invoke(game, null);

                        object gameState = Activator.CreateInstance(_typeZXGameState, $"Panorama_{themeType}_{seed}");
                        AccessTools.Property(_typeZXGameState, "GameMode")?.SetValue(gameState, ZXGameModeType.Survival);
                        AccessTools.Property(_typeZXGameState, "ChallengeType")?.SetValue(gameState, ZXGameChallengeType.Default);

                        object randParams = Activator.CreateInstance(_typeZXRandomLevelParams);
                        AccessTools.Property(_typeZXRandomLevelParams, "Seed")?.SetValue(randParams, seed);
                        AccessTools.Property(_typeZXRandomLevelParams, "NCells")?.SetValue(randParams, 256);
                        AccessTools.Property(_typeZXRandomLevelParams, "ThemeType")?.SetValue(randParams, themeType);
                        AccessTools.Property(_typeZXRandomLevelParams, "FactorZombiePopulation")?.SetValue(randParams, factorZombiePop);
                        AccessTools.Property(_typeZXRandomLevelParams, "FactorGameDuration")?.SetValue(randParams, factorDuration);
                        AccessTools.Property(_typeZXRandomLevelParams, "Name")?.SetValue(randParams, $"Panorama_{themeType}_{seed}");
                        AccessTools.Property(_typeZXGameState, "SurvivalModeParams")?.SetValue(gameState, randParams);

                        AccessTools.Method(_typeZXGameState, ObfuscatedSymbols.Method_ZXGameState_SetCurrent, new[] { _typeZXGameState })
                            ?.Invoke(null, new[] { gameState });

                        object levelState = Activator.CreateInstance(lsType);
                        AccessTools.Method(lsType, ObfuscatedSymbols.Method_ZXLevelState_SetCurrent, new[] { lsType })
                            ?.Invoke(null, new[] { levelState });
                        AccessTools.Method(lsType, ObfuscatedSymbols.Method_ZXLevelState_PreInit, Type.EmptyTypes)
                            ?.Invoke(levelState, null);

                        newSys = loadGeneric?.Invoke(null, new object[] { false });
                        AccessTools.Property(TypeZXGame, "CurrentGameSystem")?.SetValue(game, newSys);

                        Sync.CoopBalanceManager.TestOverridePreset = overridePreset;
                        Sync.CoopBalanceManager.TestOverridePlayerCount = overridePlayerCount;
                        Sync.CoopBalanceManager.TestOverrideMultiFaction = overrideMultiFaction;
                        Sync.CoopBalanceManager.TestOverrideSpawnMode = overrideSpawnMode;

                        ModLogger.Info("[GameBridge] StartSurvivalAsync worker calling MapGenerator.Generate...");
                        generated = _methodMapGeneratorGenerate.Invoke(null, new[] { randParams }) as DXLevel;
                        if (generated == null)
                        {
                            workerErr = "MapGenerator returned null";
                        }
                        else
                        {
                            ModLogger.Info($"[GameBridge] StartSurvivalAsync worker generated NCells={generated.NCells}, calling InitLevel...");
                            _mInitLevel.Invoke(newSys, new object[] { generated });
                            ModLogger.Info("[GameBridge] StartSurvivalAsync worker InitLevel completed.");
                        }
                    }
                    catch (Exception ex)
                    {
                        workerErr = ex.InnerException?.Message ?? ex.Message;
                        ModLogger.Error("[GameBridge] StartSurvivalAsync worker failed", ex);
                    }
                    finally
                    {
                        Sync.CoopBalanceManager.TestOverridePreset = null;
                        Sync.CoopBalanceManager.TestOverridePlayerCount = null;
                        Sync.CoopBalanceManager.TestOverrideMultiFaction = null;
                        Sync.CoopBalanceManager.TestOverrideSpawnMode = null;
                    }
                };

                Action finishAction = () =>
                {
                    try
                    {
                        ModLogger.Info($"[GameBridge] StartSurvivalAsync finishAction: err={workerErr ?? "none"}, hasLevel={generated != null}");
                        if (workerErr == null && generated != null && newSys != null)
                        {
                            _methodLevelSysEnterGameView?.Invoke(newSys, Type.EmptyTypes);
                            ClearLoadingFadeOverlay();
                            ClearWaitCursor();
                            if (game != null) game.Paused = true;
                            SurvivalGenLastOk = DXLevel.Current != null && CurrentLevelState != null;
                            if (!SurvivalGenLastOk)
                            {
                                SurvivalGenLastError = "DXLevel or LevelState null after EnterGameView";
                            }
                            else if (overrideMultiFaction == true && !Sync.CoopSessionManager.IsSessionActive)
                            {
                                int pCount = Math.Max(2, Math.Min(3, overridePlayerCount ?? 2));
                                Sync.CoopSessionManager.ApplySyncedMultiFactionSettings(
                                    Sync.CoopGameMode.MultiFaction,
                                    Sync.CoopSessionManager.DefeatRule);
                                Sync.FactionOwnershipTracker.AssignInitialOwnershipFromLevel(pCount);
                                Sync.FactionOwnershipTracker.SaveToLevelScriptVariables();
                                Patches.MultiWaveSwarmContext.Reset();
                            }
                            else if (!Sync.CoopSessionManager.IsSessionActive)
                            {
                                Sync.CoopSessionManager.ApplySyncedMultiFactionSettings(
                                    Sync.CoopGameMode.Archon,
                                    Sync.CoopSessionManager.DefeatRule);
                                Sync.FactionOwnershipTracker.Reset();
                            }
                        }
                        else
                        {
                            SurvivalGenLastOk = false;
                            SurvivalGenLastError = workerErr ?? "unknown worker failure";
                        }
                    }
                    catch (Exception ex2)
                    {
                        SurvivalGenLastOk = false;
                        SurvivalGenLastError = ex2.Message;
                        ModLogger.Error("[GameBridge] StartSurvivalAsync completion failed", ex2);
                    }
                    finally
                    {
                        SurvivalGenInProgress = false;
                    }
                };

                if (_propZXGameIsLoading == null)
                    _propZXGameIsLoading = AccessTools.Property(TypeZXGame, "IsLoading");

                asyncTransitionMethod.Invoke(game, new object[]
                {
                    20,
                    workerAction,
                    finishAction,
                    new List<string> { "Generating panorama comparison map..." },
                    true
                });

                return true;
            }
            catch (Exception ex)
            {
                SurvivalGenLastOk = false;
                SurvivalGenLastError = ex.Message;
                SurvivalGenInProgress = false;
                ModLogger.Error("[GameBridge] StartSurvivalAsync failed", ex);
                return false;
            }
        }

        public static Dictionary<string, object> CollectCurrentMapMetrics()
        {
            var res = new Dictionary<string, object>();
            object survivalParams = GetGameStateProperty("SurvivalModeParams");
            int survivalNCells = survivalParams != null ? GetRandomParamsNCells(survivalParams) : (DXLevel.Current?.NCells ?? 0);
            res["survivalNCells"] = survivalNCells;
            res["gridNCells"] = DXLevel.Current?.NCells ?? 0;
            res["playableAreaWidth"] = DXLevel.Current?.PlayableArea.Width ?? 0f;
            res["theme"] = survivalParams != null ? AccessTools.Property(survivalParams.GetType(), "ThemeType")?.GetValue(survivalParams)?.ToString() : "";
            res["seed"] = survivalParams != null ? AccessTools.Property(survivalParams.GetType(), "Seed")?.GetValue(survivalParams) : 0;

            GetLevelStateCommandCenterExtras(out int ccW, out int ccF);
            res["ccExtraWorkers"] = ccW;
            res["ccExtraFood"] = ccF;

            int rangers = 0, soldiers = 0, zombies = 0, giants = 0, mutants = 0;
            int zombiesPlayable = 0, t3Playable = 0, zombiesOutside = 0, t3Outside = 0;
            int weakZombies = 0, mediumZombies = 0, strongZombies = 0;
            int chubbyZombies = 0, venomZombies = 0, harpyZombies = 0;
            int doomVillages = 0, oilSources = 0, pickables = 0, trees = 0, minerals = 0;
            var nestType = GameAssembly?.GetType("ZX.Components.CInfectionNest");
            var ccEntities = new List<DXEntity>();
            var oilCells = new List<Point>();
            var zombieCells = new List<Point>();
            var doomCells = new List<Point>();

            if (DXGame.Current?.Entities != null)
            {
                foreach (var e in DXGame.Current.Entities)
                {
                    if (e == null || e.Disposed) continue;
                    string tn = e.GetType().Name;
                    if (IsCommandCenter(e)) ccEntities.Add(e);
                    if (tn == "Ranger") rangers++;
                    else if (tn == "SoldierRegular") soldiers++;
                    else if (tn == "OilSource") { oilSources++; oilCells.Add(e.Cell); }
                    else if (tn.Contains("Giant") && _typeZombie != null && _typeZombie.IsInstanceOfType(e)) { giants++; zombies++; zombieCells.Add(e.Cell); }
                    else if (tn.Contains("Mutant") && _typeZombie != null && _typeZombie.IsInstanceOfType(e)) { mutants++; zombies++; zombieCells.Add(e.Cell); }
                    else if (_typeZombie != null && _typeZombie.IsInstanceOfType(e))
                    {
                        zombies++;
                        zombieCells.Add(e.Cell);
                        bool isInPlay = DXLevel.Current == null || DXLevel.Current.IsCellInPlayableArea(e.Cell);
                        bool isT3Mob = false;
                        if (tn.StartsWith("ZombieWeak", StringComparison.Ordinal)) weakZombies++;
                        else if (tn.StartsWith("ZombieMedium", StringComparison.Ordinal) || tn.StartsWith("ZombieWorker", StringComparison.Ordinal)) mediumZombies++;
                        else if (tn.StartsWith("ZombieDressed", StringComparison.Ordinal)) strongZombies++;
                        else if (tn.StartsWith("ZombieStrong", StringComparison.Ordinal)) { chubbyZombies++; isT3Mob = true; }
                        else if (tn == "ZombieVenom") { venomZombies++; isT3Mob = true; }
                        else if (tn == "ZombieHarpy") { harpyZombies++; isT3Mob = true; }
                        if (isInPlay) { zombiesPlayable++; if (isT3Mob) t3Playable++; }
                        else { zombiesOutside++; if (isT3Mob) t3Outside++; }
                    }
                    else if (tn.Contains("Pickable") || tn.Contains("Item") || tn.Contains("Gold") && !tn.Contains("Mineral")) pickables++;
                    else if (tn.Contains("Forest") || tn.Contains("Tree")) trees++;
                    else if (tn.Contains("Mineral")) minerals++;

                    if (nestType != null && e.Components != null && e.Components.Exists(c => c != null && nestType.IsInstanceOfType(c)))
                    {
                        doomVillages++;
                        doomCells.Add(e.Cell);
                    }
                }
            }

            // Also count static map-layer mineral/forest cells from MapDrawer
            int terrainTrees = 0, terrainGold = 0, terrainStone = 0, terrainIron = 0;
            var ccDetails = new List<Dictionary<string, object>>();
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain = null;
            DXGridLayerEnum<ZXMapLayerObjectType> layerObj = null;
            int gN = 0;
            if (DXLevel.Current != null && TryGetGeneratedLevelContext(DXLevel.Current, out _, out _, out layerTerrain, out layerObj, out gN, out _))
            {
                if (layerObj != null && gN > 0)
                {
                    for (int x = 0; x < gN; x++)
                    {
                        for (int y = 0; y < gN; y++)
                        {
                            var o = layerObj[x, y];
                            if (o == ZXMapLayerObjectType.Tree) terrainTrees++;
                            else if (o == ZXMapLayerObjectType.MineralGold) terrainGold++;
                            else if (o == ZXMapLayerObjectType.MineralStone) terrainStone++;
                            else if (o == ZXMapLayerObjectType.MineralIron) terrainIron++;
                        }
                    }
                }
            }

            for (int ci = 0; ci < ccEntities.Count; ci++)
            {
                var cc = ccEntities[ci];
                int cx = cc.CellX, cy = cc.CellY;
                double minOtherDist = ccEntities.Count > 1 ? double.MaxValue : 0.0;
                for (int oi = 0; oi < ccEntities.Count; oi++)
                {
                    if (oi == ci) continue;
                    double dx = ccEntities[oi].CellX - cx;
                    double dy = ccEntities[oi].CellY - cy;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < minOtherDist) minOtherDist = d;
                }
                double distToFirst = ci == 0 ? 0.0 : Math.Sqrt((cx - ccEntities[0].CellX) * (double)(cx - ccEntities[0].CellX) + (cy - ccEntities[0].CellY) * (double)(cy - ccEntities[0].CellY));

                double minZombieDist = -1.0;
                for (int zi = 0; zi < zombieCells.Count; zi++)
                {
                    double dx = zombieCells[zi].X - cx;
                    double dy = zombieCells[zi].Y - cy;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (minZombieDist < 0.0 || d < minZombieDist) minZombieDist = d;
                }

                double minDoomDist = -1.0;
                for (int di = 0; di < doomCells.Count; di++)
                {
                    double dx = doomCells[di].X - cx;
                    double dy = doomCells[di].Y - cy;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (minDoomDist < 0.0 || d < minDoomDist) minDoomDist = d;
                }

                int oil48 = 0;
                for (int oi = 0; oi < oilCells.Count; oi++)
                {
                    long dx = oilCells[oi].X - cx;
                    long dy = oilCells[oi].Y - cy;
                    if (dx * dx + dy * dy <= 48L * 48L) oil48++;
                }

                int trees16 = 0, grass16 = 0, water16 = 0, stone24 = 0, iron48 = 0, gold30 = 0;
                if (layerObj != null && layerTerrain != null && gN > 0)
                {
                    int xMin = Math.Max(0, cx - 48), xMax = Math.Min(gN - 1, cx + 48);
                    int yMin = Math.Max(0, cy - 48), yMax = Math.Min(gN - 1, cy + 48);
                    for (int x = xMin; x <= xMax; x++)
                    {
                        int dx = x - cx;
                        for (int y = yMin; y <= yMax; y++)
                        {
                            int dy = y - cy;
                            int d2 = dx * dx + dy * dy;
                            if (d2 > 48 * 48) continue;
                            var o = layerObj[x, y];
                            var tr = layerTerrain[x, y];
                            if (d2 <= 16 * 16)
                            {
                                if (o == ZXMapLayerObjectType.Tree) trees16++;
                                if (tr == ZXMapLayerTerrainType.Grass) grass16++;
                                else if (tr == ZXMapLayerTerrainType.Water) water16++;
                            }
                            if (d2 <= 24 * 24 && o == ZXMapLayerObjectType.MineralStone) stone24++;
                            if (d2 <= 30 * 30 && o == ZXMapLayerObjectType.MineralGold) gold30++;
                            if (o == ZXMapLayerObjectType.MineralIron) iron48++;
                        }
                    }
                }

                ccDetails.Add(new Dictionary<string, object>
                {
                    ["index"] = ci + 1,
                    ["x"] = cx,
                    ["y"] = cy,
                    ["normX"] = gN > 0 ? Math.Round((double)cx / gN, 4) : 0.0,
                    ["normY"] = gN > 0 ? Math.Round((double)cy / gN, 4) : 0.0,
                    ["distToCc1"] = Math.Round(distToFirst, 1),
                    ["minOtherCcDist"] = Math.Round(minOtherDist, 1),
                    ["trees16"] = trees16,
                    ["grass16"] = grass16,
                    ["water16"] = water16,
                    ["stone24"] = stone24,
                    ["iron48"] = iron48,
                    ["gold30"] = gold30,
                    ["oil48"] = oil48,
                    ["nearestZombieDist"] = minZombieDist >= 0.0 ? Math.Round(minZombieDist, 1) : -1.0,
                    ["nearestDoomDist"] = minDoomDist >= 0.0 ? Math.Round(minDoomDist, 1) : -1.0
                });
            }

            int levelEventsCount = GetLevelCollectionCount("LevelEvents");
            bool allCcsConnected = CheckAllCcsWalkableConnected(ccEntities, layerTerrain, layerObj, gN);
            double cc1CenterDist = 0.0;
            if (ccEntities.Count > 0 && gN > 0)
            {
                double dx = ccEntities[0].CellX - gN * 0.5;
                double dy = ccEntities[0].CellY - gN * 0.5;
                cc1CenterDist = Math.Round(Math.Sqrt(dx * dx + dy * dy), 1);
            }
            res["commandCenterCount"] = ccEntities.Count;
            res["commandCenters"] = ccDetails;
            res["rangers"] = rangers;
            res["soldiers"] = soldiers;
            res["zombiesTotal"] = zombies;
            res["zombiesPlayable"] = zombiesPlayable;
            res["t3Playable"] = t3Playable;
            res["zombiesOutsidePlayable"] = zombiesOutside;
            res["t3OutsidePlayable"] = t3Outside;
            res["weakZombies"] = weakZombies;
            res["mediumZombies"] = mediumZombies;
            res["strongZombies"] = strongZombies;
            res["chubbyZombies"] = chubbyZombies;
            res["venomZombies"] = venomZombies;
            res["harpyZombies"] = harpyZombies;
            res["giants"] = giants;
            res["mutants"] = mutants;
            res["doomVillages"] = doomVillages;
            res["oilSources"] = oilSources;
            res["pickables"] = pickables;
            res["terrainTreeCells"] = terrainTrees;
            res["terrainGoldCells"] = terrainGold;
            res["terrainStoneCells"] = terrainStone;
            res["terrainIronCells"] = terrainIron;
            res["levelEventsCount"] = levelEventsCount;
            res["spawnMode"] = Sync.FactionOwnershipTracker.ActiveSpawnMode.ToString();
            res["mutationTendency"] = Sync.FactionOwnershipTracker.ActiveMutationTendency.ToString();
            res["allCcsConnected"] = allCcsConnected;
            res["cc1DistFromCenter"] = cc1CenterDist;
            return res;
        }

        private static bool CheckAllCcsWalkableConnected(
            List<DXEntity> ccs,
            DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            DXGridLayerEnum<ZXMapLayerObjectType> layerObj,
            int gN)
        {
            if (ccs == null || ccs.Count <= 1 || layerTerrain == null || layerObj == null || gN <= 0)
                return true;

            var visited = new bool[gN, gN];
            var q = new Queue<Point>(4096);
            // Seed around CC1's 5x5 footprint
            for (int dx = -3; dx <= 3; dx++)
            {
                for (int dy = -3; dy <= 3; dy++)
                {
                    int sx = ccs[0].CellX + dx;
                    int sy = ccs[0].CellY + dy;
                    if (sx > 1 && sy > 1 && sx < gN - 1 && sy < gN - 1 && !visited[sx, sy])
                    {
                        var tr = layerTerrain[sx, sy];
                        var ob = layerObj[sx, sy];
                        if ((tr == ZXMapLayerTerrainType.Earth || tr == ZXMapLayerTerrainType.Grass) &&
                            (ob == ZXMapLayerObjectType.None || ob == ZXMapLayerObjectType.MineralGold ||
                             ob == ZXMapLayerObjectType.MineralStone || ob == ZXMapLayerObjectType.MineralIron))
                        {
                            visited[sx, sy] = true;
                            q.Enqueue(new Point(sx, sy));
                        }
                    }
                }
            }

            while (q.Count > 0)
            {
                Point p = q.Dequeue();
                for (int dir = 0; dir < 4; dir++)
                {
                    int nx = p.X + (dir == 0 ? 1 : (dir == 1 ? -1 : 0));
                    int ny = p.Y + (dir == 2 ? 1 : (dir == 3 ? -1 : 0));
                    if (nx <= 1 || ny <= 1 || nx >= gN - 1 || ny >= gN - 1 || visited[nx, ny]) continue;
                    var tr = layerTerrain[nx, ny];
                    var ob = layerObj[nx, ny];
                    if ((tr != ZXMapLayerTerrainType.Earth && tr != ZXMapLayerTerrainType.Grass) ||
                        ob == ZXMapLayerObjectType.Mountain || ob == ZXMapLayerObjectType.Tree)
                        continue;
                    visited[nx, ny] = true;
                    q.Enqueue(new Point(nx, ny));
                }
            }

            for (int i = 1; i < ccs.Count; i++)
            {
                bool reached = false;
                for (int dx = -3; dx <= 3 && !reached; dx++)
                {
                    for (int dy = -3; dy <= 3 && !reached; dy++)
                    {
                        int tx = ccs[i].CellX + dx;
                        int ty = ccs[i].CellY + dy;
                        if (tx > 0 && ty > 0 && tx < gN && ty < gN && visited[tx, ty])
                            reached = true;
                    }
                }
                if (!reached) return false;
            }
            return true;
        }

        // ====================================================================
        // Phase 4 M1 Multi-Faction Helpers
        // ====================================================================

        public static bool IsCommandCenter(DXEntity entity)
        {
            return entity != null && TypeCommandCenter != null && TypeCommandCenter.IsInstanceOfType(entity);
        }

        public static bool IsBuildingInfected(DXEntity entity)
        {
            if (entity == null) return false;
            EnsureFastEconomyPerfDelegates();
            var fast = _fastGetBldgInfected;
            if (fast != null) return fast(entity);
            if (_propBuildingInfected == null || TypeBuilding == null || !TypeBuilding.IsInstanceOfType(entity))
                return false;
            try { return (bool)_propBuildingInfected.GetValue(entity); } catch { return false; }
        }

        public static bool IsEntityAlive(DXEntity entity)
        {
            if (entity == null || entity.Disposed) return false;
            object life = GetLifeObject(entity);
            if (life == null) return true;
            try
            {
                Type t = life.GetType();
                if (!_isAlivePropCache.TryGetValue(t, out PropertyInfo aliveProp))
                {
                    aliveProp = t.GetProperty("IsAlive", BindingFlags.Public | BindingFlags.Instance);
                    _isAlivePropCache[t] = aliveProp;
                }
                return aliveProp == null || (bool)aliveProp.GetValue(life);
            }
            catch { return true; }
        }

        private sealed class AliveCcCacheEntry
        {
            public int Tick;
            public int Gen;
            public DXGame Game;
            public DXEntity[] Items;
        }

        private static int _ccCacheGeneration = 1;
        private static volatile AliveCcCacheEntry _aliveCcCache;

        public static void InvalidateCommandCenterCache()
        {
            System.Threading.Interlocked.Increment(ref _ccCacheGeneration);
        }

        private static bool AreEntitiesUndisposed(DXEntity[] items)
        {
            for (int i = 0; i < items.Length; i++)
            {
                var e = items[i];
                if (e == null || e.Disposed) return false;
            }
            return true;
        }

        public static IReadOnlyList<DXEntity> GetAliveCommandCentersCached()
        {
            var game = DXGame.Current;
            if (game == null) return Array.Empty<DXEntity>();

            int tick = game.IAIterationInt;
            int gen = System.Threading.Volatile.Read(ref _ccCacheGeneration);
            var snap = _aliveCcCache;
            if (!TABCoopMod.Determinism.LockstepClock.LoadingLevel &&
                tick > 0 &&
                snap != null &&
                ReferenceEquals(snap.Game, game) &&
                snap.Gen == gen &&
                snap.Tick == tick &&
                AreEntitiesUndisposed(snap.Items))
            {
                return snap.Items;
            }

            var list = new List<DXEntity>(4);
            CollectCommandCenters(game, aliveOnly: true, list);
            DXEntity[] arr = list.Count > 0 ? list.ToArray() : Array.Empty<DXEntity>();
            _aliveCcCache = new AliveCcCacheEntry
            {
                Tick = tick,
                Gen = gen,
                Game = game,
                Items = arr
            };
            return arr;
        }

        private static void CollectCommandCenters(DXGame game, bool aliveOnly, List<DXEntity> result)
        {
            result.Clear();
            if (game == null) return;

            if (TypeCommandCenter != null)
            {
                foreach (var e in game.EntitiesOfType(TypeCommandCenter))
                {
                    if (e == null || e.Disposed) continue;
                    if (!IsPlayerEntity(e)) continue;
                    if (aliveOnly && (!IsEntityAlive(e) || IsBuildingInfected(e))) continue;
                    result.Add(e);
                }
            }
            else
            {
                var entities = GetEntitiesList(game);
                if (entities != null)
                {
                    for (int i = 0; i < entities.Count; i++)
                    {
                        var e = entities[i];
                        if (e == null || e.Disposed || !IsCommandCenter(e)) continue;
                        if (!IsPlayerEntity(e)) continue;
                        if (aliveOnly && (!IsEntityAlive(e) || IsBuildingInfected(e))) continue;
                        result.Add(e);
                    }
                }
                else if (game.Entities != null)
                {
                    foreach (var e in game.Entities)
                    {
                        if (e == null || e.Disposed || !IsCommandCenter(e)) continue;
                        if (!IsPlayerEntity(e)) continue;
                        if (aliveOnly && (!IsEntityAlive(e) || IsBuildingInfected(e))) continue;
                        result.Add(e);
                    }
                }
            }

            if (result.Count > 1)
            {
                result.Sort((a, b) =>
                {
                    int cx = a.CellX.CompareTo(b.CellX);
                    if (cx != 0) return cx;
                    int cy = a.CellY.CompareTo(b.CellY);
                    if (cy != 0) return cy;
                    return a.ID.CompareTo(b.ID);
                });
            }
        }

        public static List<DXEntity> GetAllCommandCenters(bool aliveOnly = true)
        {
            if (aliveOnly)
                return new List<DXEntity>(GetAliveCommandCentersCached());

            var result = new List<DXEntity>(4);
            var game = DXGame.Current;
            if (game == null) return result;
            CollectCommandCenters(game, aliveOnly: false, result);
            return result;
        }

        public static DXEntity GetLevelSysCurrentCommandCenter()
        {
            var lvlSys = LevelSystemInstance;
            if (lvlSys == null || _fieldLevelSysCurrentCC == null) return null;
            try { return _fieldLevelSysCurrentCC.GetValue(lvlSys) as DXEntity; } catch { return null; }
        }

        public static void SetLevelSysCurrentCommandCenter(DXEntity cc)
        {
            var lvlSys = LevelSystemInstance;
            if (lvlSys == null || _fieldLevelSysCurrentCC == null) return;
            try { _fieldLevelSysCurrentCC.SetValue(lvlSys, cc); } catch { }
        }

        public static void RefreshLevelStateCommandCenter(DXEntity preferredCc = null)
        {
            var ls = CurrentLevelState;
            if (ls == null) return;
            try
            {
                if (preferredCc != null && !preferredCc.Disposed)
                {
                    _fieldLevelStateCachedCC?.SetValue(ls, preferredCc);
                    _propLevelStateCurrentCCCell?.SetValue(ls, (Point?)preferredCc.Cell);
                    _propLevelStateInfectedDestiny?.SetValue(ls, preferredCc.Cell);
                }
                else
                {
                    _fieldLevelStateCachedCC?.SetValue(ls, null);
                    _methodLevelStateRefreshCCCell?.Invoke(ls, null);
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] RefreshLevelStateCommandCenter failed: {ex.Message}");
            }
        }

        public static bool SetLevelStateCurrentCommandCenterCell(Point? cell, out Point? previous)
        {
            previous = null;
            var ls = CurrentLevelState;
            if (ls == null || _propLevelStateCurrentCCCell == null) return false;
            try
            {
                previous = (Point?)_propLevelStateCurrentCCCell.GetValue(ls);
                _propLevelStateCurrentCCCell.SetValue(ls, cell);
                return true;
            }
            catch { return false; }
        }

        public static void SelectOnlyEntity(DXEntity entity)
        {
            if (entity == null || entity.Disposed || _mUnselectAll == null || _mGetEntitySel == null) return;
            try
            {
                _mUnselectAll.Invoke(null, null);
                object sel = _mGetEntitySel.Invoke(entity, null);
                if (sel != null)
                {
                    var prop = sel.GetType().GetProperty("Selected", BindingFlags.Public | BindingFlags.Instance);
                    prop?.SetValue(sel, true);
                }
                _methodLevelSysRefreshCmdPanel?.Invoke(LevelSystemInstance, null);
            }
            catch { }
        }

        public static void UnselectAllEntities()
        {
            try
            {
                _mUnselectAll?.Invoke(null, null);
                _methodLevelSysRefreshCmdPanel?.Invoke(LevelSystemInstance, null);
            }
            catch { }
        }

        public static bool IsEntitySelected(DXEntity entity)
        {
            if (entity == null || _fieldSelectedList == null) return false;
            foreach (object item in EnumerateSelectedComponents())
            {
                if ((item as DXComponent)?.Entity == entity)
                    return true;
            }
            return false;
        }

        public static IDictionary GetLevelScriptVariables()
        {
            var ls = CurrentLevelState;
            if (ls == null || _propLevelStateScriptVars == null) return null;
            try { return _propLevelStateScriptVars.GetValue(ls) as IDictionary; } catch { return null; }
        }

        public static bool TryGetEnergyGridState(
            object mapLayers,
            out uint[,] energyGrid,
            out List<Point> frontier,
            out IList sources,
            out int lastIdx)
        {
            energyGrid = null;
            frontier = null;
            sources = null;
            lastIdx = 0;
            if (mapLayers == null || _fieldMapLayersEnergyGrid == null || _fieldMapLayersEnergyFrontier == null || _fieldMapLayersEnergySources == null)
                return false;
            try
            {
                energyGrid = _fieldMapLayersEnergyGrid.GetValue(mapLayers) as uint[,];
                frontier = _fieldMapLayersEnergyFrontier.GetValue(mapLayers) as List<Point>;
                sources = _fieldMapLayersEnergySources.GetValue(mapLayers) as IList;
                lastIdx = _fieldMapLayersEnergyLastIdx != null ? Convert.ToInt32(_fieldMapLayersEnergyLastIdx.GetValue(mapLayers)) : 0;
                return energyGrid != null && frontier != null && sources != null;
            }
            catch { return false; }
        }

        public static void SetEnergyLastIdx(object mapLayers, int idx)
        {
            if (mapLayers == null || _fieldMapLayersEnergyLastIdx == null) return;
            try { _fieldMapLayersEnergyLastIdx.SetValue(mapLayers, idx); } catch { }
        }

        public static void RebuildEnergyGridNow()
        {
            var ml = GetMapLayersInstance();
            if (ml == null || _methodMapLayersRebuildEnergyGrid == null) return;
            try { _methodMapLayersRebuildEnergyGrid.Invoke(ml, null); } catch { }
        }

        public static uint[,] GetMapLayersEnergyGrid(object mapLayers)
        {
            if (mapLayers == null || _fieldMapLayersEnergyGrid == null) return null;
            try { return _fieldMapLayersEnergyGrid.GetValue(mapLayers) as uint[,]; } catch { return null; }
        }

        public static bool GetMapLayersEnergyInitDone(object mapLayers)
        {
            if (mapLayers == null || _fieldMapLayersEnergyInitDone == null) return false;
            try { return (bool)_fieldMapLayersEnergyInitDone.GetValue(mapLayers); } catch { return false; }
        }

        public static void SetMapLayersEnergyInitDone(object mapLayers, bool value)
        {
            if (mapLayers == null || _fieldMapLayersEnergyInitDone == null) return;
            try { _fieldMapLayersEnergyInitDone.SetValue(mapLayers, value); } catch { }
        }

        public static DXSimpleLock GetMapLayersEnergyLock(object mapLayers)
        {
            if (mapLayers == null || _fieldMapLayersEnergyLock == null) return null;
            try { return _fieldMapLayersEnergyLock.GetValue(mapLayers) as DXSimpleLock; } catch { return null; }
        }

        public static void NotifyMapLayersEnergyChanged(object mapLayers, bool isPreview)
        {
            if (mapLayers == null) return;
            try
            {
                if (!isPreview && _propMapLayersNEnergyChanges != null)
                {
                    uint cur = Convert.ToUInt32(_propMapLayersNEnergyChanges.GetValue(mapLayers));
                    _propMapLayersNEnergyChanges.SetValue(mapLayers, cur + 1u);
                }
                _propMapLayersLayerEnergyUpdated?.SetValue(mapLayers, true);
            }
            catch { }
        }

        public static bool InvokeMapLayersHasPathToAnyCC(object mapLayers, Point cell)
        {
            if (mapLayers == null || _methodMapLayersHasPathToAnyCC == null) return true;
            try { return (bool)_methodMapLayersHasPathToAnyCC.Invoke(mapLayers, new object[] { cell }); }
            catch { return true; }
        }

        public static bool InvokeMapLayersHasPathBetween(object mapLayers, Point a, Point b)
        {
            if (mapLayers == null || _methodMapLayersHasPathBetween == null) return false;
            try { return (bool)_methodMapLayersHasPathBetween.Invoke(mapLayers, new object[] { a, b }); }
            catch { return false; }
        }

        public static bool InvokeLevelSysIsCellPlayableForEnergy(int x, int y)
        {
            var ls = LevelSystemInstance;
            if (ls == null || _methodLevelSysIsCellPlayableForEnergy == null) return true;
            try { return (bool)_methodLevelSysIsCellPlayableForEnergy.Invoke(ls, new object[] { x, y }); }
            catch { return true; }
        }

        public static void InvokeCEnergyTransfererUpdateGrid(object component)
        {
            if (component == null || _methodEnergyTransfererUpdateGrid == null) return;
            try { _methodEnergyTransfererUpdateGrid.Invoke(component, null); } catch { }
        }

        public static void SetBuildingHasEnergyConnection(object building, bool value)
        {
            if (building == null || _fieldBuildingHasEnergyConn == null) return;
            try { _fieldBuildingHasEnergyConn.SetValue(building, value); } catch { }
        }

        public static void InvokeBuildingRefreshEnergy(object building, bool force)
        {
            if (building == null || _methodBuildingRefreshEnergy == null) return;
            try { _methodBuildingRefreshEnergy.Invoke(building, new object[] { force }); } catch { }
        }

        public static void InvokeStructureUpdateEnabled(object structure)
        {
            if (structure == null || _methodStructureUpdateEnabled == null) return;
            try { _methodStructureUpdateEnabled.Invoke(structure, null); } catch { }
        }

        public static int GetEntityEnergyTransferRadius(DXEntity entity)
        {
            if (entity == null) return 0;
            var fast = _fastGetEnergyTransferRadius;
            if (fast != null)
                return fast(entity);

            if (_methodEntityGetDefaultParams == null || _propDefaultParamsEnergyRadius == null) return 0;
            try
            {
                object p = _methodEntityGetDefaultParams.Invoke(entity, null);
                return p != null ? Convert.ToInt32(_propDefaultParamsEnergyRadius.GetValue(p)) : 0;
            }
            catch { return 0; }
        }

        public static uint GetEntityPathfindingMask(DXEntity entity)
        {
            if (entity == null) return uint.MaxValue;
            var fast = _fastGetPathfindingMask;
            if (fast != null) return fast(entity);
            return uint.MaxValue;
        }

        public static int GetMovableCellSize(object movableComp)
        {
            if (movableComp == null) return 1;
            var fast = _fastGetMovableCellSize;
            if (fast != null) return fast(movableComp);
            return 1;
        }

        public static PointF GetCommandTargetPosition(object cmdTarget)
        {
            if (cmdTarget == null) return PointF.Empty;
            var fast = _fastGetCmdTargetPos;
            if (fast != null) return fast(cmdTarget);
            EnsureCommandTargetMeta();
            return (_pSerPosition?.GetValue(cmdTarget) is PointF p) ? p : PointF.Empty;
        }

        public static ZXTerrainResourceType[,] GetTerrainResourceCells()
        {
            try
            {
                if (_methodTerrainSystemGetCurrent == null)
                {
                    var tTerr = GameAssembly?.GetType(ObfuscatedSymbols.Type_TerrainSystem);
                    if (tTerr != null)
                    {
                        _methodTerrainSystemGetCurrent = AccessTools.Method(tTerr, ObfuscatedSymbols.Method_TerrainSystem_GetCurrent, Type.EmptyTypes);
                        _propTerrainSystemCells = tTerr.GetProperty(ObfuscatedSymbols.Prop_TerrainSystem_Cells, BindingFlags.Public | BindingFlags.Instance);
                    }
                }
                object inst = _methodTerrainSystemGetCurrent?.Invoke(null, null);
                if (inst == null || _propTerrainSystemCells == null) return null;
                return _propTerrainSystemCells.GetValue(inst) as ZXTerrainResourceType[,];
            }
            catch { return null; }
        }

        public static DXEntity GetBDataOwner(object bData)
        {
            if (bData == null || _methodBDataGetOwner == null) return null;
            try { return _methodBDataGetOwner.Invoke(bData, null) as DXEntity; } catch { return null; }
        }

        public static DXEntity GetBDataTargetEntity(object bData)
        {
            if (bData == null || _fieldBDataTargetRef == null) return null;
            try
            {
                object boxedRef = _fieldBDataTargetRef.GetValue(bData);
                if (boxedRef == null) return null;
                if (_propEntityRefEntity == null)
                    _propEntityRefEntity = boxedRef.GetType().GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
                return _propEntityRefEntity?.GetValue(boxedRef) as DXEntity;
            }
            catch { return null; }
        }

        public static void SetBDataTargetEntity(object bData, DXEntity target)
        {
            if (bData == null || _fieldBDataTargetRef == null) return;
            try
            {
                object boxedRef = Activator.CreateInstance(_fieldBDataTargetRef.FieldType);
                if (_propEntityRefId == null)
                    _propEntityRefId = _fieldBDataTargetRef.FieldType.GetProperty("IDEntity", BindingFlags.Public | BindingFlags.Instance);
                _propEntityRefId?.SetValue(boxedRef, target != null ? target.ID : 0UL);
                _fieldBDataTargetRef.SetValue(bData, boxedRef);
            }
            catch { }
        }

        public static PointF? GetBDataTargetPosition(object bData)
        {
            if (bData == null || _propBDataTargetPos == null) return null;
            try { return (PointF?)_propBDataTargetPos.GetValue(bData); } catch { return null; }
        }

        public static void SetBDataTargetPosition(object bData, PointF? pos)
        {
            if (bData == null || _propBDataTargetPos == null) return;
            try { _propBDataTargetPos.SetValue(bData, pos); } catch { }
        }

        public static void SetEntityTeamAndRelation(DXEntity ent, ZXTeamType team, ZXRelationshipType relation)
        {
            if (ent == null) return;
            try
            {
                if (_propEntityRelation != null)
                    _propEntityRelation.SetValue(ent, Enum.ToObject(_propEntityRelation.PropertyType, (int)relation));
                if (_propEntityTeam != null)
                    _propEntityTeam.SetValue(ent, Enum.ToObject(_propEntityTeam.PropertyType, (int)team));
            }
            catch { }
        }

        public static void NotifyLocalPlayerBannerAndSound(string text, Color color, int durationMs = 3500)
        {
            ShowTopBanner(text, color, durationMs);
        }

        // ====================================================================
        // Phase 4 M2 & M3 Multi-Faction Economy / Tech / Mayor / Visuals
        // ====================================================================

        public struct RawLevelEconomyState
        {
            public int Wood, Stone, Iron, Oil, Gold, ResourceStorage;
            public int FoodSupply, WorkersSupply, EnergySupply, FoodNeeded, WorkersNeeded, EnergyNeeded;
            public int Colonists, GoldByCc, TotalStock;
            public int ExtraFood, ExtraWorkers, ExtraEnergy;
            public int FoodReserved, WorkersReserved, EnergyReserved;
            public int WoodProd, StoneProd, IronProd, OilProd, GoldProd;
            public int WoodProdRes, StoneProdRes, IronProdRes, OilProdRes;
            public int NegGoldTurns, NegFoodTurns, NMarkets, MaxGoldExport, MaxColonists;
            public int ArmyGold, ArmyFoodPen, StructGold;
            public float GoldPerColonist;
            public bool ShowFullMap;
        }

        private delegate void FastReadEconomyDelegate(object ls, out RawLevelEconomyState s);
        private delegate void FastWriteEconomyDelegate(object ls, in RawLevelEconomyState s);
        private delegate void FastReadBuildingTotalsDelegate(
            object bldg,
            out int foodSup, out int workSup, out int engSup,
            out int woodSup, out int stoneSup, out int ironSup,
            out int oilSup, out int goldSup, out int foodNeed);
        private delegate void FastReadDefaultParamsDelegate(
            object dp,
            out bool canEnhance, out float resGenTime, out bool convertGold,
            out int colonists, out int resStorage, out int goldGen, out float goldPerCol,
            out int foodNeed, out int workNeed, out int engNeed,
            out int woodGen, out int stoneGen, out int ironGen, out int oilGen,
            out int resCollectRadius, out int goldCost, out int level,
            out int maxInstances, out float returnFactor);

        private static FastReadEconomyDelegate _fastReadEconomy;
        private static FastWriteEconomyDelegate _fastWriteEconomy;
        private static FastReadBuildingTotalsDelegate _fastReadBldgTotals;
        private static FastReadDefaultParamsDelegate _fastReadDefaultParams;
        private static Func<object, object> _fastGetEntityDefaultParams;
        private static Func<object, bool> _fastGetBldgDisabledManual;
        private static Func<object, bool> _fastGetBldgCanSupply;
        private static Func<object, bool> _fastGetBldgInfected;
        private static bool _fastEconPerfCompiled;

        private static void EnsureFastEconomyPerfDelegates()
        {
            if (_fastEconPerfCompiled) return;
            _fastEconPerfCompiled = true;
            try
            {
                var lsType = LevelStateType ?? GameAssembly?.GetType("ZX.ZXLevelState");
                if (lsType != null)
                {
                    var fieldPairs = new (FieldInfo src, string dstName)[]
                    {
                        (_fLsWood, nameof(RawLevelEconomyState.Wood)),
                        (_fLsStone, nameof(RawLevelEconomyState.Stone)),
                        (_fLsIron, nameof(RawLevelEconomyState.Iron)),
                        (_fLsOil, nameof(RawLevelEconomyState.Oil)),
                        (_fLsGold, nameof(RawLevelEconomyState.Gold)),
                        (_fLsStorage, nameof(RawLevelEconomyState.ResourceStorage)),
                        (_fLsFoodSup, nameof(RawLevelEconomyState.FoodSupply)),
                        (_fLsWorkSup, nameof(RawLevelEconomyState.WorkersSupply)),
                        (_fLsEngSup, nameof(RawLevelEconomyState.EnergySupply)),
                        (_fLsFoodNeed, nameof(RawLevelEconomyState.FoodNeeded)),
                        (_fLsWorkNeed, nameof(RawLevelEconomyState.WorkersNeeded)),
                        (_fLsEngNeed, nameof(RawLevelEconomyState.EnergyNeeded)),
                        (_fLsColonists, nameof(RawLevelEconomyState.Colonists)),
                        (_fLsGoldByCc, nameof(RawLevelEconomyState.GoldByCc)),
                        (_fLsTotalStock, nameof(RawLevelEconomyState.TotalStock)),
                    };
                    var propPairs = new (PropertyInfo src, string dstName)[]
                    {
                        (_propLevelStateExtraFood, nameof(RawLevelEconomyState.ExtraFood)),
                        (_propLevelStateExtraWorkers, nameof(RawLevelEconomyState.ExtraWorkers)),
                        (_pLsExtraEnergy, nameof(RawLevelEconomyState.ExtraEnergy)),
                        (_pLsFoodRes, nameof(RawLevelEconomyState.FoodReserved)),
                        (_pLsWorkRes, nameof(RawLevelEconomyState.WorkersReserved)),
                        (_pLsEngRes, nameof(RawLevelEconomyState.EnergyReserved)),
                        (_pLsWoodProd, nameof(RawLevelEconomyState.WoodProd)),
                        (_pLsStoneProd, nameof(RawLevelEconomyState.StoneProd)),
                        (_pLsIronProd, nameof(RawLevelEconomyState.IronProd)),
                        (_pLsOilProd, nameof(RawLevelEconomyState.OilProd)),
                        (_pLsGoldProd, nameof(RawLevelEconomyState.GoldProd)),
                        (_pLsWoodProdRes, nameof(RawLevelEconomyState.WoodProdRes)),
                        (_pLsStoneProdRes, nameof(RawLevelEconomyState.StoneProdRes)),
                        (_pLsIronProdRes, nameof(RawLevelEconomyState.IronProdRes)),
                        (_pLsOilProdRes, nameof(RawLevelEconomyState.OilProdRes)),
                        (_pLsNegGoldTurns, nameof(RawLevelEconomyState.NegGoldTurns)),
                        (_pLsNegFoodTurns, nameof(RawLevelEconomyState.NegFoodTurns)),
                        (_pLsNMarkets, nameof(RawLevelEconomyState.NMarkets)),
                        (_pLsMaxGoldExport, nameof(RawLevelEconomyState.MaxGoldExport)),
                        (_pLsMaxColonists, nameof(RawLevelEconomyState.MaxColonists)),
                        (_pLsArmyGold, nameof(RawLevelEconomyState.ArmyGold)),
                        (_pLsArmyFoodPen, nameof(RawLevelEconomyState.ArmyFoodPen)),
                        (_pLsStructGold, nameof(RawLevelEconomyState.StructGold)),
                        (_pLsGoldPerCol, nameof(RawLevelEconomyState.GoldPerColonist)),
                        (_pLsShowFullMap, nameof(RawLevelEconomyState.ShowFullMap)),
                    };

                    bool allTypesMatch = true;
                    foreach (var p in fieldPairs)
                    {
                        if (p.src == null || typeof(RawLevelEconomyState).GetField(p.dstName)?.FieldType != p.src.FieldType)
                        { allTypesMatch = false; break; }
                    }
                    if (allTypesMatch)
                    {
                        foreach (var p in propPairs)
                        {
                            var dstF = typeof(RawLevelEconomyState).GetField(p.dstName);
                            if (p.src == null) continue;
                            if (dstF == null || dstF.FieldType != p.src.PropertyType || p.src.GetGetMethod(true) == null || p.src.GetSetMethod(true) == null)
                            { allTypesMatch = false; break; }
                        }
                    }

                    if (allTypesMatch)
                    {
                        var dmR = new DynamicMethod(
                            "TABCoop_FastReadRawEconomy",
                            typeof(void),
                            new[] { typeof(object), typeof(RawLevelEconomyState).MakeByRefType() },
                            typeof(GameBridge).Module,
                            skipVisibility: true);
                        var ilR = dmR.GetILGenerator();
                        var locR = ilR.DeclareLocal(lsType);
                        ilR.Emit(OpCodes.Ldarg_1);
                        ilR.Emit(OpCodes.Initobj, typeof(RawLevelEconomyState));
                        ilR.Emit(OpCodes.Ldarg_0);
                        ilR.Emit(OpCodes.Castclass, lsType);
                        ilR.Emit(OpCodes.Stloc, locR);
                        foreach (var p in fieldPairs)
                        {
                            var dstF = typeof(RawLevelEconomyState).GetField(p.dstName);
                            ilR.Emit(OpCodes.Ldarg_1);
                            ilR.Emit(OpCodes.Ldloc, locR);
                            ilR.Emit(OpCodes.Ldfld, p.src);
                            ilR.Emit(OpCodes.Stfld, dstF);
                        }
                        foreach (var p in propPairs)
                        {
                            if (p.src == null) continue;
                            var dstF = typeof(RawLevelEconomyState).GetField(p.dstName);
                            ilR.Emit(OpCodes.Ldarg_1);
                            ilR.Emit(OpCodes.Ldloc, locR);
                            ilR.Emit(OpCodes.Callvirt, p.src.GetGetMethod(true));
                            ilR.Emit(OpCodes.Stfld, dstF);
                        }
                        ilR.Emit(OpCodes.Ret);
                        _fastReadEconomy = (FastReadEconomyDelegate)dmR.CreateDelegate(typeof(FastReadEconomyDelegate));

                        var dmW = new DynamicMethod(
                            "TABCoop_FastWriteRawEconomy",
                            typeof(void),
                            new[] { typeof(object), typeof(RawLevelEconomyState).MakeByRefType() },
                            typeof(GameBridge).Module,
                            skipVisibility: true);
                        var ilW = dmW.GetILGenerator();
                        var locW = ilW.DeclareLocal(lsType);
                        ilW.Emit(OpCodes.Ldarg_0);
                        ilW.Emit(OpCodes.Castclass, lsType);
                        ilW.Emit(OpCodes.Stloc, locW);
                        foreach (var p in fieldPairs)
                        {
                            var srcF = typeof(RawLevelEconomyState).GetField(p.dstName);
                            ilW.Emit(OpCodes.Ldloc, locW);
                            ilW.Emit(OpCodes.Ldarg_1);
                            ilW.Emit(OpCodes.Ldfld, srcF);
                            ilW.Emit(OpCodes.Stfld, p.src);
                        }
                        foreach (var p in propPairs)
                        {
                            if (p.src == null) continue;
                            var srcF = typeof(RawLevelEconomyState).GetField(p.dstName);
                            ilW.Emit(OpCodes.Ldloc, locW);
                            ilW.Emit(OpCodes.Ldarg_1);
                            ilW.Emit(OpCodes.Ldfld, srcF);
                            ilW.Emit(OpCodes.Callvirt, p.src.GetSetMethod(true));
                        }
                        ilW.Emit(OpCodes.Ret);
                        _fastWriteEconomy = (FastWriteEconomyDelegate)dmW.CreateDelegate(typeof(FastWriteEconomyDelegate));
                    }
                }

                if (TypeZXEntity != null && _methodEntityGetDefaultParams != null)
                {
                    var dmDpObj = new DynamicMethod(
                        "TABCoop_GetEntityDefaultParamsObj",
                        typeof(object),
                        new[] { typeof(object) },
                        typeof(GameBridge).Module,
                        skipVisibility: true);
                    var ilDpObj = dmDpObj.GetILGenerator();
                    var lblNull = ilDpObj.DefineLabel();
                    ilDpObj.Emit(OpCodes.Ldarg_0);
                    ilDpObj.Emit(OpCodes.Isinst, TypeZXEntity);
                    ilDpObj.Emit(OpCodes.Dup);
                    ilDpObj.Emit(OpCodes.Brfalse_S, lblNull);
                    ilDpObj.Emit(OpCodes.Callvirt, _methodEntityGetDefaultParams);
                    ilDpObj.Emit(OpCodes.Ret);
                    ilDpObj.MarkLabel(lblNull);
                    ilDpObj.Emit(OpCodes.Ret);
                    _fastGetEntityDefaultParams = (Func<object, object>)dmDpObj.CreateDelegate(typeof(Func<object, object>));
                }

                if (TypeBuilding != null)
                {
                    _fastGetBldgDisabledManual = CompileBoolPropGetter(TypeBuilding, _pBldgDisabledManual, "TABCoop_FastBldgDisabledManual");
                    _fastGetBldgCanSupply = CompileBoolPropGetter(TypeBuilding, _pBldgCanSupply, "TABCoop_FastBldgCanSupply");
                    _fastGetBldgInfected = CompileBoolPropGetter(TypeBuilding, _propBuildingInfected, "TABCoop_FastBldgInfected");

                    var bldgProps = new[]
                    {
                        _pBldgTotalFoodSup, _pBldgTotalWorkSup, _pBldgTotalEngSup,
                        _pBldgTotalWoodSup, _pBldgTotalStoneSup, _pBldgTotalIronSup,
                        _pBldgTotalOilSup, _pBldgTotalGoldSup, _pBldgTotalFoodNeed
                    };
                    bool okB = true;
                    foreach (var bp in bldgProps)
                        if (bp == null || bp.PropertyType != typeof(int) || bp.GetGetMethod(true) == null) { okB = false; break; }
                    if (okB)
                    {
                        var pTypes = new Type[10];
                        pTypes[0] = typeof(object);
                        for (int i = 1; i < 10; i++) pTypes[i] = typeof(int).MakeByRefType();
                        var dmB = new DynamicMethod("TABCoop_FastReadBuildingTotals", typeof(void), pTypes, typeof(GameBridge).Module, skipVisibility: true);
                        var ilB = dmB.GetILGenerator();
                        var locB = ilB.DeclareLocal(TypeBuilding);
                        ilB.Emit(OpCodes.Ldarg_0);
                        ilB.Emit(OpCodes.Castclass, TypeBuilding);
                        ilB.Emit(OpCodes.Stloc, locB);
                        for (int i = 0; i < bldgProps.Length; i++)
                        {
                            ilB.Emit(OpCodes.Ldarg, i + 1);
                            ilB.Emit(OpCodes.Ldloc, locB);
                            ilB.Emit(OpCodes.Callvirt, bldgProps[i].GetGetMethod(true));
                            ilB.Emit(OpCodes.Stind_I4);
                        }
                        ilB.Emit(OpCodes.Ret);
                        _fastReadBldgTotals = (FastReadBuildingTotalsDelegate)dmB.CreateDelegate(typeof(FastReadBuildingTotalsDelegate));
                    }
                }

                var dpType = _methodEntityGetDefaultParams?.ReturnType;
                if (dpType != null)
                {
                    var dpProps = new (PropertyInfo p, Type expected)[]
                    {
                        (_pDefCanEnhance, typeof(bool)),
                        (_pDefResGenTime, typeof(float)),
                        (_pDefConvertResToGold, typeof(bool)),
                        (_pDefColonists, typeof(int)),
                        (_pDefResStorage, typeof(int)),
                        (_pDefGoldGen, typeof(int)),
                        (_pDefGoldPerCol, typeof(float)),
                        (_pDefFoodNeed, typeof(int)),
                        (_pDefWorkNeed, typeof(int)),
                        (_pDefEngNeed, typeof(int)),
                        (_pDefWoodGen, typeof(int)),
                        (_pDefStoneGen, typeof(int)),
                        (_pDefIronGen, typeof(int)),
                        (_pDefOilGen, typeof(int)),
                        (_pDefResCollectRadius, typeof(int)),
                        (_pDefGoldCost, typeof(int)),
                        (_pDefLevel, typeof(int)),
                        (_pDefMaxInstances, typeof(int)),
                        (_pDefReturnFactor, typeof(float)),
                    };
                    bool okDp = true;
                    foreach (var pair in dpProps)
                        if (pair.p == null || pair.p.PropertyType != pair.expected || pair.p.GetGetMethod(true) == null) { okDp = false; break; }
                    if (okDp)
                    {
                        var pTypes = new Type[20];
                        pTypes[0] = typeof(object);
                        for (int i = 0; i < dpProps.Length; i++) pTypes[i + 1] = dpProps[i].expected.MakeByRefType();
                        var dmDp = new DynamicMethod("TABCoop_FastReadDefaultParams", typeof(void), pTypes, typeof(GameBridge).Module, skipVisibility: true);
                        var ilDp = dmDp.GetILGenerator();
                        var locDp = ilDp.DeclareLocal(dpType);
                        ilDp.Emit(OpCodes.Ldarg_0);
                        ilDp.Emit(OpCodes.Castclass, dpType);
                        ilDp.Emit(OpCodes.Stloc, locDp);
                        for (int i = 0; i < dpProps.Length; i++)
                        {
                            ilDp.Emit(OpCodes.Ldarg, i + 1);
                            ilDp.Emit(OpCodes.Ldloc, locDp);
                            ilDp.Emit(OpCodes.Callvirt, dpProps[i].p.GetGetMethod(true));
                            Type t = dpProps[i].expected;
                            if (t == typeof(bool)) ilDp.Emit(OpCodes.Stind_I1);
                            else if (t == typeof(float)) ilDp.Emit(OpCodes.Stind_R4);
                            else ilDp.Emit(OpCodes.Stind_I4);
                        }
                        ilDp.Emit(OpCodes.Ret);
                        _fastReadDefaultParams = (FastReadDefaultParamsDelegate)dmDp.CreateDelegate(typeof(FastReadDefaultParamsDelegate));
                    }
                }

                if (_fastReadEconomy != null && _fastWriteEconomy != null && _fastReadBldgTotals != null && _fastReadDefaultParams != null)
                    ModLogger.Info("[Perf] Compiled DynamicMethods for FastEconomy/Building/DefaultParams.");
            }
            catch (Exception ex)
            {
                _fastReadEconomy = null;
                _fastWriteEconomy = null;
                _fastReadBldgTotals = null;
                _fastReadDefaultParams = null;
                ModLogger.Warn($"[Perf] EnsureFastEconomyPerfDelegates failed (falling back to reflection): {ex.Message}");
            }
        }

        private static Func<object, bool> CompileBoolPropGetter(Type targetType, PropertyInfo prop, string dmName)
        {
            var getter = prop?.GetGetMethod(true);
            if (targetType == null || getter == null || prop.PropertyType != typeof(bool)) return null;
            var dm = new DynamicMethod(dmName, typeof(bool), new[] { typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
            var il = dm.GetILGenerator();
            var lblFalse = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Isinst, targetType);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Brfalse_S, lblFalse);
            il.Emit(OpCodes.Callvirt, getter);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(lblFalse);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            return (Func<object, bool>)dm.CreateDelegate(typeof(Func<object, bool>));
        }

        public static bool ReadRawLevelEconomyState(out RawLevelEconomyState s)
        {
            s = default;
            object ls = CurrentLevelState;
            if (ls == null) return false;
            EnsureFastEconomyPerfDelegates();
            var fast = _fastReadEconomy;
            if (fast != null)
            {
                fast(ls, out s);
                return true;
            }
            try
            {
                s.Wood = (int)_fLsWood.GetValue(ls);
                s.Stone = (int)_fLsStone.GetValue(ls);
                s.Iron = (int)_fLsIron.GetValue(ls);
                s.Oil = (int)_fLsOil.GetValue(ls);
                s.Gold = (int)_fLsGold.GetValue(ls);
                s.ResourceStorage = (int)_fLsStorage.GetValue(ls);
                s.FoodSupply = (int)_fLsFoodSup.GetValue(ls);
                s.WorkersSupply = (int)_fLsWorkSup.GetValue(ls);
                s.EnergySupply = (int)_fLsEngSup.GetValue(ls);
                s.FoodNeeded = (int)_fLsFoodNeed.GetValue(ls);
                s.WorkersNeeded = (int)_fLsWorkNeed.GetValue(ls);
                s.EnergyNeeded = (int)_fLsEngNeed.GetValue(ls);
                s.Colonists = (int)_fLsColonists.GetValue(ls);
                s.GoldByCc = (int)_fLsGoldByCc.GetValue(ls);
                s.TotalStock = (int)_fLsTotalStock.GetValue(ls);

                s.ExtraFood = _propLevelStateExtraFood != null ? (int)_propLevelStateExtraFood.GetValue(ls) : 0;
                s.ExtraWorkers = _propLevelStateExtraWorkers != null ? (int)_propLevelStateExtraWorkers.GetValue(ls) : 0;
                s.ExtraEnergy = _pLsExtraEnergy != null ? (int)_pLsExtraEnergy.GetValue(ls) : 0;
                s.FoodReserved = (int)_pLsFoodRes.GetValue(ls);
                s.WorkersReserved = (int)_pLsWorkRes.GetValue(ls);
                s.EnergyReserved = (int)_pLsEngRes.GetValue(ls);
                s.WoodProd = (int)_pLsWoodProd.GetValue(ls);
                s.StoneProd = (int)_pLsStoneProd.GetValue(ls);
                s.IronProd = (int)_pLsIronProd.GetValue(ls);
                s.OilProd = (int)_pLsOilProd.GetValue(ls);
                s.GoldProd = (int)_pLsGoldProd.GetValue(ls);
                s.WoodProdRes = (int)_pLsWoodProdRes.GetValue(ls);
                s.StoneProdRes = (int)_pLsStoneProdRes.GetValue(ls);
                s.IronProdRes = (int)_pLsIronProdRes.GetValue(ls);
                s.OilProdRes = (int)_pLsOilProdRes.GetValue(ls);
                s.NegGoldTurns = (int)_pLsNegGoldTurns.GetValue(ls);
                s.NegFoodTurns = (int)_pLsNegFoodTurns.GetValue(ls);
                s.NMarkets = (int)_pLsNMarkets.GetValue(ls);
                s.MaxGoldExport = (int)_pLsMaxGoldExport.GetValue(ls);
                s.MaxColonists = (int)_pLsMaxColonists.GetValue(ls);
                s.ArmyGold = (int)_pLsArmyGold.GetValue(ls);
                s.ArmyFoodPen = (int)_pLsArmyFoodPen.GetValue(ls);
                s.StructGold = (int)_pLsStructGold.GetValue(ls);
                s.GoldPerColonist = (float)_pLsGoldPerCol.GetValue(ls);
                s.ShowFullMap = _pLsShowFullMap != null && (bool)_pLsShowFullMap.GetValue(ls);
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] ReadRawLevelEconomyState failed: {ex.Message}");
                return false;
            }
        }

        public static void WriteRawLevelEconomyState(in RawLevelEconomyState s)
        {
            object ls = CurrentLevelState;
            if (ls == null) return;
            EnsureFastEconomyPerfDelegates();
            var fast = _fastWriteEconomy;
            if (fast != null)
            {
                fast(ls, in s);
                return;
            }
            try
            {
                _fLsWood.SetValue(ls, s.Wood);
                _fLsStone.SetValue(ls, s.Stone);
                _fLsIron.SetValue(ls, s.Iron);
                _fLsOil.SetValue(ls, s.Oil);
                _fLsGold.SetValue(ls, s.Gold);
                _fLsStorage.SetValue(ls, s.ResourceStorage);
                _fLsFoodSup.SetValue(ls, s.FoodSupply);
                _fLsWorkSup.SetValue(ls, s.WorkersSupply);
                _fLsEngSup.SetValue(ls, s.EnergySupply);
                _fLsFoodNeed.SetValue(ls, s.FoodNeeded);
                _fLsWorkNeed.SetValue(ls, s.WorkersNeeded);
                _fLsEngNeed.SetValue(ls, s.EnergyNeeded);
                _fLsColonists.SetValue(ls, s.Colonists);
                _fLsGoldByCc.SetValue(ls, s.GoldByCc);
                _fLsTotalStock.SetValue(ls, s.TotalStock);

                _propLevelStateExtraFood?.SetValue(ls, s.ExtraFood);
                _propLevelStateExtraWorkers?.SetValue(ls, s.ExtraWorkers);
                _pLsExtraEnergy?.SetValue(ls, s.ExtraEnergy);
                _pLsFoodRes.SetValue(ls, s.FoodReserved);
                _pLsWorkRes.SetValue(ls, s.WorkersReserved);
                _pLsEngRes.SetValue(ls, s.EnergyReserved);
                _pLsWoodProd.SetValue(ls, s.WoodProd);
                _pLsStoneProd.SetValue(ls, s.StoneProd);
                _pLsIronProd.SetValue(ls, s.IronProd);
                _pLsOilProd.SetValue(ls, s.OilProd);
                _pLsGoldProd.SetValue(ls, s.GoldProd);
                _pLsWoodProdRes.SetValue(ls, s.WoodProdRes);
                _pLsStoneProdRes.SetValue(ls, s.StoneProdRes);
                _pLsIronProdRes.SetValue(ls, s.IronProdRes);
                _pLsOilProdRes.SetValue(ls, s.OilProdRes);
                _pLsNegGoldTurns.SetValue(ls, s.NegGoldTurns);
                _pLsNegFoodTurns.SetValue(ls, s.NegFoodTurns);
                _pLsNMarkets.SetValue(ls, s.NMarkets);
                _pLsMaxGoldExport.SetValue(ls, s.MaxGoldExport);
                _pLsMaxColonists.SetValue(ls, s.MaxColonists);
                _pLsArmyGold.SetValue(ls, s.ArmyGold);
                _pLsArmyFoodPen.SetValue(ls, s.ArmyFoodPen);
                _pLsStructGold.SetValue(ls, s.StructGold);
                _pLsGoldPerCol.SetValue(ls, s.GoldPerColonist);
                if (_pLsShowFullMap != null) _pLsShowFullMap.SetValue(ls, s.ShowFullMap);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] WriteRawLevelEconomyState failed: {ex.Message}");
            }
        }

        public static Dictionary<ulong, int> GetLevelBonusTemplatesDict()
        {
            object ls = CurrentLevelState;
            return ls != null ? _pLsBonusTemplates?.GetValue(ls) as Dictionary<ulong, int> : null;
        }

        public static void SetLevelBonusTemplatesDict(Dictionary<ulong, int> dict)
        {
            object ls = CurrentLevelState;
            if (ls != null && dict != null)
                _pLsBonusTemplates?.SetValue(ls, dict);
        }

        public static Dictionary<int, int> GetLevelOnlyOneCommandsDict()
        {
            object ls = CurrentLevelState;
            return ls != null ? _pLsOnlyOneCmds?.GetValue(ls) as Dictionary<int, int> : null;
        }

        public static void SetLevelOnlyOneCommandsDict(Dictionary<int, int> dict)
        {
            object ls = CurrentLevelState;
            if (ls != null && dict != null)
                _pLsOnlyOneCmds?.SetValue(ls, dict);
        }

        public static void SetLevelStateExtraStats(int extraFood, int extraWorkers, int extraEnergy)
        {
            object ls = CurrentLevelState;
            if (ls == null) return;
            _propLevelStateExtraFood?.SetValue(ls, extraFood);
            _propLevelStateExtraWorkers?.SetValue(ls, extraWorkers);
            _pLsExtraEnergy?.SetValue(ls, extraEnergy);
        }

        public static void GetLevelStateExtraStats(out int extraFood, out int extraWorkers, out int extraEnergy)
        {
            object ls = CurrentLevelState;
            if (ls == null) { extraFood = extraWorkers = extraEnergy = 0; return; }
            extraFood = _propLevelStateExtraFood != null ? (int)_propLevelStateExtraFood.GetValue(ls) : 0;
            extraWorkers = _propLevelStateExtraWorkers != null ? (int)_propLevelStateExtraWorkers.GetValue(ls) : 0;
            extraEnergy = _pLsExtraEnergy != null ? (int)_pLsExtraEnergy.GetValue(ls) : 0;
        }

        public static object GetLevelStateCachedCurrentCC()
        {
            object ls = CurrentLevelState;
            return ls != null ? _pLsCurrentCc?.GetValue(ls) : null;
        }

        public static void SetLevelStateShowFullMap(bool show)
        {
            object ls = CurrentLevelState;
            if (ls != null && _pLsShowFullMap != null)
                _pLsShowFullMap.SetValue(ls, show);
        }

        public static bool GetLevelStateShowFullMap()
        {
            object ls = CurrentLevelState;
            return ls != null && _pLsShowFullMap != null && (bool)_pLsShowFullMap.GetValue(ls);
        }

        public static void CallLevelStateRecalculate()
        {
            object ls = CurrentLevelState;
            if (ls != null) _mLsRecalculate?.Invoke(ls, null);
        }

        private static PropertyInfo _propZXOrderCommand;
        private static MethodInfo _methodTrainReserve;
        private static MethodInfo _methodTrainUnreserve;
        private static FieldInfo _fieldBuildingEnhancerCacheIter;
        private static bool _enhancerCacheFieldChecked;

        /// <summary>
        /// Invalidates Building's cached Render_Iteration>>4 enhancer-building stamp (#=zlFi$Ybidu8UdzEFiptU0xNuJEJ7o)
        /// so Building.UnderEnhancerBuilding recomputes deterministically on simulation ticks rather than lagging by local render FPS.
        /// </summary>
        public static void InvalidateBuildingEnhancerCache(DXEntity building)
        {
            if (building == null || TypeBuilding == null) return;
            if (!_enhancerCacheFieldChecked)
            {
                _enhancerCacheFieldChecked = true;
                _fieldBuildingEnhancerCacheIter = TypeBuilding.GetField("#=zlFi$Ybidu8UdzEFiptU0xNuJEJ7o",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (_fieldBuildingEnhancerCacheIter != null && TypeBuilding.IsInstanceOfType(building))
                _fieldBuildingEnhancerCacheIter.SetValue(building, -1);
        }

        public static void InvalidateAllBuildingsEnhancerCache()
        {
            var game = DXGame.Current;
            if (game == null || TypeBuilding == null) return;
            if (!_enhancerCacheFieldChecked)
            {
                _enhancerCacheFieldChecked = true;
                _fieldBuildingEnhancerCacheIter = TypeBuilding.GetField("#=zlFi$Ybidu8UdzEFiptU0xNuJEJ7o",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (_fieldBuildingEnhancerCacheIter == null) return;
            foreach (var ent in game.EntitiesOfType(TypeBuilding))
            {
                if (ent != null && !ent.Disposed)
                    _fieldBuildingEnhancerCacheIter.SetValue(ent, -1);
            }
        }

        public static bool ApplyLevelReservationDeltas(
            int dFoodRes, int dWorkRes, int dEngRes,
            int dWoodProdRes, int dStoneProdRes, int dIronProdRes, int dOilProdRes)
        {
            if (dFoodRes == 0 && dWorkRes == 0 && dEngRes == 0
                && dWoodProdRes == 0 && dStoneProdRes == 0 && dIronProdRes == 0 && dOilProdRes == 0)
            {
                return false;
            }
            if (!ReadRawLevelEconomyState(out var s)) return false;
            s.FoodReserved = Math.Max(0, s.FoodReserved + dFoodRes);
            s.WorkersReserved = Math.Max(0, s.WorkersReserved + dWorkRes);
            s.EnergyReserved = Math.Max(0, s.EnergyReserved + dEngRes);
            s.WoodProdRes = Math.Max(0, s.WoodProdRes + dWoodProdRes);
            s.StoneProdRes = Math.Max(0, s.StoneProdRes + dStoneProdRes);
            s.IronProdRes = Math.Max(0, s.IronProdRes + dIronProdRes);
            s.OilProdRes = Math.Max(0, s.OilProdRes + dOilProdRes);
            WriteRawLevelEconomyState(in s);
            return true;
        }

        public static void CallLevelStateSetEnhancerBuildings(IList buildings)
        {
            object ls = CurrentLevelState;
            if (ls == null || _mLsSetEnhancerBuildings == null || TypeBuilding == null) return;
            try
            {
                var typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(TypeBuilding));
                if (buildings != null)
                {
                    for (int i = 0; i < buildings.Count; i++)
                    {
                        if (TypeBuilding.IsInstanceOfType(buildings[i]))
                            typedList.Add(buildings[i]);
                    }
                }
                _mLsSetEnhancerBuildings.Invoke(ls, new object[] { typedList });
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] SetEnhancerBuildings failed: {ex.Message}");
            }
        }

        public static float GetLevelSellFactor(int resourceIndex)
        {
            object ls = CurrentLevelState;
            if (ls == null) return 0f;
            try
            {
                switch (resourceIndex)
                {
                    case 0: return _mLsSellWood != null ? (float)_mLsSellWood.Invoke(ls, null) : 0f;
                    case 1: return _mLsSellStone != null ? (float)_mLsSellStone.Invoke(ls, null) : 0f;
                    case 2: return _mLsSellIron != null ? (float)_mLsSellIron.Invoke(ls, null) : 0f;
                    case 3: return _mLsSellOil != null ? (float)_mLsSellOil.Invoke(ls, null) : 0f;
                    default: return 0f;
                }
            }
            catch { return 0f; }
        }

        public static void AddLevelGoldDirect(int delta)
        {
            object ls = CurrentLevelState;
            if (ls != null) _mLsAddGoldDirect?.Invoke(ls, new object[] { delta });
        }

        public static void AddLevelResourceWithMarket(int resType, int delta)
        {
            object ls = CurrentLevelState;
            if (ls == null || delta == 0) return;
            switch (resType)
            {
                case 0: _mLsAddGoldDirect?.Invoke(ls, new object[] { delta }); break;
                case 1: _mLsAddWoodMarket?.Invoke(ls, new object[] { delta }); break;
                case 2: _mLsAddStoneMarket?.Invoke(ls, new object[] { delta }); break;
                case 3: _mLsAddIronMarket?.Invoke(ls, new object[] { delta }); break;
                case 4: _mLsAddOilMarket?.Invoke(ls, new object[] { delta }); break;
            }
        }

        public static void UnlockOnlyOneCommandById(int commandId)
        {
            object ls = CurrentLevelState;
            if (ls == null || commandId == 0) return;
            var dict = GetLevelOnlyOneCommandsDict();
            if (dict != null && !dict.ContainsKey(commandId))
                dict[commandId] = 1;
        }

        public static bool IsOnlyOneCommandUnlocked(object cmd)
        {
            object ls = CurrentLevelState;
            if (ls == null || cmd == null || _mLsHasOnlyOne == null) return false;
            try { return (bool)_mLsHasOnlyOne.Invoke(ls, new[] { cmd }); }
            catch { return false; }
        }

        public static object GetCommandByType(Type cmdType)
        {
            if (cmdType == null) return null;
            return GetCommandGeneric(cmdType);
        }

        public static void RefreshCommandPanelNow()
        {
            try
            {
                object sys = LevelSystemInstance;
                if (sys != null) _methodLevelSysRefreshCmdPanel?.Invoke(sys, null);
            }
            catch { }
        }

        public static void PlayLocalSoundEffect(string soundId)
        {
            // Keep silent/safe fallback when audio singleton isn't needed.
        }

        public static bool TryGetCommandResourcesCost(object cmd, object executorEntity, out int gold, out int wood, out int stone, out int iron, out int oil, out int food, out int workers, out int energy)
        {
            gold = wood = stone = iron = oil = food = workers = energy = 0;
            if (cmd == null || _mCmdGetCost == null) return false;
            try
            {
                object res = _mCmdGetCost.Invoke(cmd, new[] { executorEntity });
                if (res == null) return false;
                ReadResourcesCostBoxed(res, out gold, out wood, out stone, out iron, out oil, out food, out workers, out energy);
                return true;
            }
            catch { return false; }
        }

        public static bool TryGetEntityResourcesCost(Type entityType, out int gold, out int wood, out int stone, out int iron, out int oil, out int food, out int workers, out int energy)
        {
            gold = wood = stone = iron = oil = food = workers = energy = 0;
            if (entityType == null || _mResCostForType == null) return false;
            try
            {
                object res = _mResCostForType.Invoke(null, new object[] { entityType });
                if (res == null) return false;
                ReadResourcesCostBoxed(res, out gold, out wood, out stone, out iron, out oil, out food, out workers, out energy);
                return true;
            }
            catch { return false; }
        }

        public static void ReadResourcesCostBoxed(object res, out int gold, out int wood, out int stone, out int iron, out int oil, out int food, out int workers, out int energy)
        {
            gold = _pResGold != null ? (int)_pResGold.GetValue(res) : 0;
            wood = _pResWood != null ? (int)_pResWood.GetValue(res) : 0;
            stone = _pResStone != null ? (int)_pResStone.GetValue(res) : 0;
            iron = _pResIron != null ? (int)_pResIron.GetValue(res) : 0;
            oil = _pResOil != null ? (int)_pResOil.GetValue(res) : 0;
            food = _pResFood != null ? (int)_pResFood.GetValue(res) : 0;
            workers = _pResWorkers != null ? (int)_pResWorkers.GetValue(res) : 0;
            energy = _pResEnergy != null ? (int)_pResEnergy.GetValue(res) : 0;
        }

        public static object WriteResourcesCostBoxed(object templateCost, int gold, int wood, int stone, int iron, int oil, int food, int workers, int energy)
        {
            object box = templateCost ?? Activator.CreateInstance(TypeZXResourcesCost);
            _pResGold?.SetValue(box, gold);
            _pResWood?.SetValue(box, wood);
            _pResStone?.SetValue(box, stone);
            _pResIron?.SetValue(box, iron);
            _pResOil?.SetValue(box, oil);
            _pResFood?.SetValue(box, food);
            _pResWorkers?.SetValue(box, workers);
            _pResEnergy?.SetValue(box, energy);
            return box;
        }

        public static object GetEntityDefaultParamsBoxed(DXEntity entity)
        {
            if (entity == null) return null;
            EnsureFastEconomyPerfDelegates();
            var fast = _fastGetEntityDefaultParams;
            if (fast != null) return fast(entity);
            if (_methodEntityGetDefaultParams == null) return null;
            try { return _methodEntityGetDefaultParams.Invoke(entity, null); } catch { return null; }
        }

        public static bool IsCommandCenterTelescopeEnabled(DXEntity cc)
        {
            if (cc == null || _pCcTelescopeEnabled == null) return false;
            try { return (bool)_pCcTelescopeEnabled.GetValue(cc); } catch { return false; }
        }

        public static bool GetBuildingDisabledManually(DXEntity bldg)
        {
            if (bldg == null) return false;
            EnsureFastEconomyPerfDelegates();
            var fast = _fastGetBldgDisabledManual;
            if (fast != null) return fast(bldg);
            if (_pBldgDisabledManual == null) return false;
            try { return (bool)_pBldgDisabledManual.GetValue(bldg); } catch { return false; }
        }

        public static bool GetBuildingCanSupplyResources(DXEntity bldg)
        {
            if (bldg == null) return false;
            EnsureFastEconomyPerfDelegates();
            var fast = _fastGetBldgCanSupply;
            if (fast != null) return fast(bldg);
            if (_pBldgCanSupply == null) return false;
            try { return (bool)_pBldgCanSupply.GetValue(bldg); } catch { return false; }
        }

        public static void ReadBuildingTotals(DXEntity bldg, out int foodSup, out int workSup, out int engSup, out int woodSup, out int stoneSup, out int ironSup, out int oilSup, out int goldSup, out int foodNeed)
        {
            EnsureFastEconomyPerfDelegates();
            var fast = _fastReadBldgTotals;
            if (fast != null && bldg != null)
            {
                fast(bldg, out foodSup, out workSup, out engSup, out woodSup, out stoneSup, out ironSup, out oilSup, out goldSup, out foodNeed);
                return;
            }
            foodSup = (int)_pBldgTotalFoodSup.GetValue(bldg);
            workSup = (int)_pBldgTotalWorkSup.GetValue(bldg);
            engSup = (int)_pBldgTotalEngSup.GetValue(bldg);
            woodSup = (int)_pBldgTotalWoodSup.GetValue(bldg);
            stoneSup = (int)_pBldgTotalStoneSup.GetValue(bldg);
            ironSup = (int)_pBldgTotalIronSup.GetValue(bldg);
            oilSup = (int)_pBldgTotalOilSup.GetValue(bldg);
            goldSup = (int)_pBldgTotalGoldSup.GetValue(bldg);
            foodNeed = (int)_pBldgTotalFoodNeed.GetValue(bldg);
        }

        public static void ScaleBuildingCachedEnergySupply(DXEntity bldg, int numerator, int denominator)
        {
            if (bldg == null || _fBldgResCost == null || _pResEnergy == null) return;
            try
            {
                object cost = _fBldgResCost.GetValue(bldg);
                if (cost == null) return;
                int baseEng = (int)_pResEnergy.GetValue(cost);
                int scaled = (baseEng * numerator) / denominator;
                _pResEnergy.SetValue(cost, scaled);
                _fBldgResCost.SetValue(bldg, cost);
            }
            catch { }
        }

        public static void ReadDefaultParamsStats(
            object dp,
            out bool canEnhance, out float resGenTime, out bool convertGold,
            out int colonists, out int resStorage, out int goldGen, out float goldPerCol,
            out int foodNeed, out int workNeed, out int engNeed,
            out int woodGen, out int stoneGen, out int ironGen, out int oilGen,
            out int resCollectRadius, out int goldCost, out int level,
            out int maxInstances, out float returnFactor)
        {
            EnsureFastEconomyPerfDelegates();
            var fast = _fastReadDefaultParams;
            if (fast != null && dp != null)
            {
                fast(dp,
                    out canEnhance, out resGenTime, out convertGold,
                    out colonists, out resStorage, out goldGen, out goldPerCol,
                    out foodNeed, out workNeed, out engNeed,
                    out woodGen, out stoneGen, out ironGen, out oilGen,
                    out resCollectRadius, out goldCost, out level,
                    out maxInstances, out returnFactor);
                return;
            }
            canEnhance = (bool)_pDefCanEnhance.GetValue(dp);
            resGenTime = (float)_pDefResGenTime.GetValue(dp);
            convertGold = (bool)_pDefConvertResToGold.GetValue(dp);
            colonists = (int)_pDefColonists.GetValue(dp);
            resStorage = (int)_pDefResStorage.GetValue(dp);
            goldGen = (int)_pDefGoldGen.GetValue(dp);
            goldPerCol = (float)_pDefGoldPerCol.GetValue(dp);
            foodNeed = (int)_pDefFoodNeed.GetValue(dp);
            workNeed = (int)_pDefWorkNeed.GetValue(dp);
            engNeed = (int)_pDefEngNeed.GetValue(dp);
            woodGen = (int)_pDefWoodGen.GetValue(dp);
            stoneGen = (int)_pDefStoneGen.GetValue(dp);
            ironGen = (int)_pDefIronGen.GetValue(dp);
            oilGen = (int)_pDefOilGen.GetValue(dp);
            resCollectRadius = (int)_pDefResCollectRadius.GetValue(dp);
            goldCost = (int)_pDefGoldCost.GetValue(dp);
            level = (int)_pDefLevel.GetValue(dp);
            maxInstances = (int)_pDefMaxInstances.GetValue(dp);
            returnFactor = (float)_pDefReturnFactor.GetValue(dp);
        }

        public static void ApplyZeroFeeMayorToRegistry()
        {
            EnsureMayorMeta();
            if (TypeZXMayor == null || _mayorRegistryField == null) return;
            var dict = _mayorRegistryField.GetValue(null) as IDictionary;
            if (dict == null || dict.Contains(Sync.FactionContextManager.CoopMayorZeroFeeId)) return;

            try
            {
                object mayor = Activator.CreateInstance(TypeZXMayor);
                _pMayorIdProp?.SetValue(mayor, Sync.FactionContextManager.CoopMayorZeroFeeId);
                _pMayorNameProp?.SetValue(mayor, "Trade Guild Envoy (0% Transfer Fee)");
                _pMayorQuoteProp?.SetValue(mayor, "Free trade binds our colonies together.");
                _pMayorLevelProp?.SetValue(mayor, 1);
                _pMayorActiveProp?.SetValue(mayor, true);
                _pMayorCustomProp?.SetValue(mayor, false);
                _pMayorExtraGold?.SetValue(mayor, 0);
                _pMayorExtraWood?.SetValue(mayor, 0);
                _pMayorExtraStone?.SetValue(mayor, 0);
                _pMayorExtraIron?.SetValue(mayor, 0);
                _pMayorExtraOil?.SetValue(mayor, 0);
                _pMayorBonusTechs?.SetValue(mayor, Activator.CreateInstance(_pMayorBonusTechs.PropertyType));
                _pMayorBonusEntities?.SetValue(mayor, Activator.CreateInstance(_pMayorBonusEntities.PropertyType));
                dict.Add(Sync.FactionContextManager.CoopMayorZeroFeeId, mayor);
                ModLogger.Info($"[GameBridge] Registered custom 0-fee Mayor '{Sync.FactionContextManager.CoopMayorZeroFeeId}' into ZXMayor registry.");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] Failed to register custom 0-fee Mayor: {ex.Message}");
            }
        }

        public static int GetEntityWorkSupplyFallback(object dp)
        {
            if (dp == null || _pDefWorkSupply == null) return 0;
            return (int)_pDefWorkSupply.GetValue(dp);
        }

        public static void ReadEntityBuildLimits(object dp, out int maxInstances, out int minColonists, out Type buildingRequisite)
        {
            if (dp == null)
            {
                maxInstances = 0;
                minColonists = 0;
                buildingRequisite = null;
                return;
            }
            maxInstances = _pDefMaxInstances != null ? (int)_pDefMaxInstances.GetValue(dp) : 0;
            minColonists = _pDefMinColonists != null ? (int)_pDefMinColonists.GetValue(dp) : 0;
            buildingRequisite = _pDefBuildingRequisite?.GetValue(dp) as Type;
        }

        public static string GetEntityTemplateDisplayName(Type entType)
        {
            if (entType == null) return string.Empty;
            try
            {
                var tmpl = DXGame.Current?.GetEntityFromType(entType) as DXEntity;
                object dp = tmpl != null ? GetEntityDefaultParamsBoxed(tmpl) : null;
                string name = dp != null ? _pDefEntityName?.GetValue(dp) as string : null;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return entType.Name;
        }

        public static Type GetCommandBuildingRequisite(object cmd)
        {
            if (cmd == null || _mCmdGetDefaultParams == null || _pCmdDefBuildingRequisite == null)
                return null;
            object dp = _mCmdGetDefaultParams.Invoke(cmd, null);
            return dp != null ? _pCmdDefBuildingRequisite.GetValue(dp) as Type : null;
        }

        public static void SetCommandErrorMessage(object cmd, string msg)
        {
            if (cmd == null || _fCmdErrorMsg == null) return;
            _fCmdErrorMsg.SetValue(cmd, msg);
        }

        public static bool HasBuildingOnlyOnceCommandsExecuted(object building)
        {
            if (building == null || _pBldgOnlyOnceCmdsExecuted == null) return false;
            var list = _pBldgOnlyOnceCmdsExecuted.GetValue(building) as System.Collections.ICollection;
            return list != null && list.Count > 0;
        }

        public static void RefreshEntityFactionColor(DXEntity entity, Color c)
        {
            if (entity == null || _fEntityCachedColor == null || _fEntityBaseColor == null) return;
            try
            {
                _fEntityBaseColor.SetValue(entity, c);
                _fEntityCachedColor.SetValue(entity, (Color?)c);
            }
            catch { }
        }

        public static Type FindEntityTypeByName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName) || GameAssembly == null) return null;
            return GameAssembly.GetType(fullName);
        }

        public static Type FindCommandTypeByName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName) || GameAssembly == null) return null;
            return GameAssembly.GetType(fullName);
        }

        public static object GetCommandInstanceByType(Type cmdType)
        {
            return cmdType != null ? GetCommandGeneric(cmdType) : null;
        }

        public static bool GetCommandCenterTelescopeEnabled(object cc)
        {
            if (cc == null || _pCcTelescopeEnabled == null) return false;
            try { return (bool)_pCcTelescopeEnabled.GetValue(cc); }
            catch { return false; }
        }

        public static bool IsBuildingBeingBuilt(object bldg)
        {
            if (bldg == null || _propStructureIsBeingBuilt == null) return false;
            try { return (bool)_propStructureIsBeingBuilt.GetValue(bldg); }
            catch { return false; }
        }

        public static void CallBuildingRecalculateNear(object bldg)
        {
            if (bldg == null || _methodBuildingRecalcResources == null) return;
            try { _methodBuildingRecalcResources.Invoke(bldg, null); } catch { }
        }

        public static void ApplyBuildingEnergyOverride(object bldg, int newTotalEnergy)
        {
            if (bldg == null || _fBldgResCost == null || _pResEnergy == null) return;
            try
            {
                object boxed = _fBldgResCost.GetValue(bldg);
                if (boxed == null) return;
                _pResEnergy.SetValue(boxed, newTotalEnergy);
                _fBldgResCost.SetValue(bldg, boxed);
            }
            catch { }
        }

        public static void AddLevelResource(Sync.ResourceTransferKind res, int delta)
        {
            if (delta == 0) return;
            if (!ReadRawLevelEconomyState(out var e)) return;
            switch (res)
            {
                case Sync.ResourceTransferKind.Gold:
                    e.Gold = Math.Max(-10000, Math.Min(Math.Max(50, e.ResourceStorage) * 40, e.Gold + delta));
                    break;
                case Sync.ResourceTransferKind.Wood:
                    e.Wood = Math.Max(0, Math.Min(Math.Max(50, e.ResourceStorage), e.Wood + delta));
                    break;
                case Sync.ResourceTransferKind.Stone:
                    e.Stone = Math.Max(0, Math.Min(Math.Max(50, e.ResourceStorage), e.Stone + delta));
                    break;
                case Sync.ResourceTransferKind.Iron:
                    e.Iron = Math.Max(0, Math.Min(Math.Max(50, e.ResourceStorage), e.Iron + delta));
                    break;
                case Sync.ResourceTransferKind.Oil:
                    e.Oil = Math.Max(0, Math.Min(Math.Max(50, e.ResourceStorage), e.Oil + delta));
                    break;
            }
            WriteRawLevelEconomyState(in e);
        }

        public static float GetBuildableFactor(object cBuildable)
        {
            if (cBuildable == null || _propCBuildableFactor == null) return 0f;
            try { return Convert.ToSingle(_propCBuildableFactor.GetValue(cBuildable)); }
            catch { return 0f; }
        }

        public static string GetMayorId(object mayorObj)
        {
            EnsureMayorMeta();
            if (mayorObj == null || _mayorIdProp == null) return null;
            try { return _mayorIdProp.GetValue(mayorObj) as string; }
            catch { return null; }
        }

        public static string GetMayorName(object mayorObj)
        {
            EnsureMayorMeta();
            if (mayorObj == null || _mayorNameProp == null) return null;
            try { return _mayorNameProp.GetValue(mayorObj) as string; }
            catch { return null; }
        }

        public static Type TypeCBuildable => _typeCBuildable;

        private static Type _tHuman, _tDefMachine, _tDwelling, _tCInsideBuilding;
        private static PropertyInfo _pStructEnabled, _pDwellColAvail, _pDwellByUpg, _pDwellTmpl, _pTmplEntity;
        private static MethodInfo _mLsSetNeedsRecalc;
        private static PropertyInfo _pDefWoodCost, _pDefStoneCost, _pDefIronCost, _pDefOilCost;
        private static bool _dpResourceCostsResolved;

        public static bool PushZeroCostOnDefaultParams(object dp, out int origGold, out int origWood, out int origStone, out int origIron, out int origOil)
        {
            origGold = origWood = origStone = origIron = origOil = 0;
            if (dp == null || _pDefGoldCost == null) return false;
            if (!_dpResourceCostsResolved)
            {
                _dpResourceCostsResolved = true;
                Type t = dp.GetType();
                _pDefWoodCost = t.GetProperty("WoodCost", BindingFlags.Public | BindingFlags.Instance);
                _pDefStoneCost = t.GetProperty("StoneCost", BindingFlags.Public | BindingFlags.Instance);
                _pDefIronCost = t.GetProperty("IronCost", BindingFlags.Public | BindingFlags.Instance);
                _pDefOilCost = t.GetProperty("OilCost", BindingFlags.Public | BindingFlags.Instance);
            }
            try
            {
                origGold = (int)_pDefGoldCost.GetValue(dp);
                origWood = _pDefWoodCost != null ? (int)_pDefWoodCost.GetValue(dp) : 0;
                origStone = _pDefStoneCost != null ? (int)_pDefStoneCost.GetValue(dp) : 0;
                origIron = _pDefIronCost != null ? (int)_pDefIronCost.GetValue(dp) : 0;
                origOil = _pDefOilCost != null ? (int)_pDefOilCost.GetValue(dp) : 0;
                _pDefGoldCost.SetValue(dp, 0);
                _pDefWoodCost?.SetValue(dp, 0);
                _pDefStoneCost?.SetValue(dp, 0);
                _pDefIronCost?.SetValue(dp, 0);
                _pDefOilCost?.SetValue(dp, 0);
                return true;
            }
            catch { return false; }
        }

        public static void PopZeroCostOnDefaultParams(object dp, int origGold, int origWood, int origStone, int origIron, int origOil)
        {
            if (dp == null || _pDefGoldCost == null) return;
            try
            {
                _pDefGoldCost.SetValue(dp, origGold);
                _pDefWoodCost?.SetValue(dp, origWood);
                _pDefStoneCost?.SetValue(dp, origStone);
                _pDefIronCost?.SetValue(dp, origIron);
                _pDefOilCost?.SetValue(dp, origOil);
            }
            catch { }
        }

        public static bool IsEntityEnabled(DXEntity ent)
        {
            if (ent == null) return false;
            var fast = _fastIsEntityEnabled;
            if (fast != null)
                return fast(ent);

            if (_pStructEnabled == null && TypeZXEntity != null)
                _pStructEnabled = TypeZXEntity.GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
            if (_pStructEnabled == null) return false;
            try { return (bool)_pStructEnabled.GetValue(ent); } catch { return false; }
        }

        public static Type TypeHuman => _tHuman ?? (_tHuman = GameAssembly?.GetType("ZX.Entities.Human"));
        public static Type TypeDefensiveMachine => _tDefMachine ?? (_tDefMachine = GameAssembly?.GetType("ZX.Entities.DeffensiveMachine"));
        public static Type TypeUnit => _typeUnit;

        public static bool IsHumanEntity(DXEntity ent)
        {
            if (ent == null) return false;
            var t = TypeHuman;
            return t != null && t.IsInstanceOfType(ent);
        }

        public static bool IsDefensiveMachineEntity(DXEntity ent)
        {
            if (ent == null) return false;
            var t = TypeDefensiveMachine;
            return t != null && t.IsInstanceOfType(ent);
        }

        public static bool IsUnitEntity(DXEntity ent)
        {
            return ent != null && _typeUnit != null && _typeUnit.IsInstanceOfType(ent);
        }

        public static bool HasInsideBuildingComponent(DXEntity ent)
        {
            if (ent == null) return false;
            if (_tCInsideBuilding == null) _tCInsideBuilding = GameAssembly?.GetType("ZX.Components.CInsideBuilding");
            return _tCInsideBuilding != null && HasComponent(ent, _tCInsideBuilding);
        }

        public static DXEntity TryGetDwellingPreUpgradeTemplateEntity(DXEntity ent)
        {
            if (ent == null) return null;
            if (_tDwelling == null) _tDwelling = GameAssembly?.GetType("ZX.Entities.Dwelling");
            if (_tDwelling == null || !_tDwelling.IsInstanceOfType(ent)) return null;
            if (_pDwellColAvail == null)
            {
                _pDwellColAvail = _tDwelling.GetProperty("ColonistsAvailable", BindingFlags.Public | BindingFlags.Instance);
                _pDwellByUpg = _tDwelling.GetProperty("CreatedByUpgrading", BindingFlags.Public | BindingFlags.Instance);
                _pDwellTmpl = _tDwelling.GetProperty("TemplateThatCanUpgradeToThis", BindingFlags.Public | BindingFlags.Instance);
            }
            if (_pDwellColAvail == null || _pDwellByUpg == null || _pDwellTmpl == null) return null;
            try
            {
                bool colAvail = (bool)_pDwellColAvail.GetValue(ent);
                bool byUpg = (bool)_pDwellByUpg.GetValue(ent);
                if (colAvail || !byUpg) return null;
                object tmpl = _pDwellTmpl.GetValue(ent);
                if (tmpl == null) return null;
                if (_pTmplEntity == null)
                    _pTmplEntity = tmpl.GetType().GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
                var tmplEnt = _pTmplEntity?.GetValue(tmpl) as DXEntity;
                return tmplEnt != null ? DXGame.Current?.GetEntityFromType(tmplEnt.GetType()) as DXEntity : null;
            }
            catch { return null; }
        }

        public static void SetLevelStateNeedsRecalculate(bool dirty)
        {
            if (_mLsSetNeedsRecalc == null && LevelStateType != null)
                _mLsSetNeedsRecalc = AccessTools.Method(LevelStateType, "#=zHvnkp4e5mPxDjXPACA==", new[] { typeof(bool) });
            try { _mLsSetNeedsRecalc?.Invoke(null, new object[] { dirty }); } catch { }
        }

        public static void SetZeroFeeMayorActive(bool active)
        {
            EnsureMayorMeta();
            ApplyZeroFeeMayorToRegistry();
            var dict = _mayorRegistryField?.GetValue(null) as IDictionary;
            if (dict == null || !dict.Contains(Sync.FactionContextManager.CoopMayorZeroFeeId)) return;
            try { _pMayorActiveProp?.SetValue(dict[Sync.FactionContextManager.CoopMayorZeroFeeId], active); }
            catch { }
        }

        // ====================================================================
        // Phase 4 M4 Multi-Base Map Generator & Multi-Wave Swarm Helpers
        // ====================================================================

        public static DXGridLayerEnum<ZXMapLayerZombies> GetMapDrawerZombieLayer(object mapDrawer)
        {
            if (mapDrawer == null || _propMapDrawerLayerZombies == null) return null;
            return _propMapDrawerLayerZombies.GetValue(mapDrawer) as DXGridLayerEnum<ZXMapLayerZombies>;
        }

        public static DXRange<int> GetMapThemeNumDoomVillages(object themeObj)
        {
            if (themeObj == null || _propMapThemeNumDoomVillages == null) return default(DXRange<int>);
            return (DXRange<int>)_propMapThemeNumDoomVillages.GetValue(themeObj);
        }

        public static void SetMapThemeNumDoomVillages(object themeObj, DXRange<int> range)
        {
            if (themeObj == null || _propMapThemeNumDoomVillages == null) return;
            _propMapThemeNumDoomVillages.SetValue(themeObj, range);
        }

        public static bool IsInfectionNestEntity(DXEntity entity)
        {
            return entity != null && _typeCInfectionNest != null && HasComponent(entity, _typeCInfectionNest);
        }

        public static void SetInfectionNestFactor(DXEntity entity, float factor)
        {
            if (entity == null || _typeCInfectionNest == null || _propInfectionNestConfig == null || _propInfectionNestFactor == null)
                return;
            var comp = FindComponent(entity, _typeCInfectionNest);
            object cfg = comp != null ? _propInfectionNestConfig.GetValue(comp) : null;
            if (cfg != null)
                _propInfectionNestFactor.SetValue(cfg, factor);
        }

        public static DXEntityTemplate GetDoomNestTemplate(int tier)
        {
            MethodInfo m = tier >= 2 ? _methodCatalogDoomNestLarge : (tier == 1 ? _methodCatalogDoomNestMedium : _methodCatalogDoomNestSmall);
            return m?.Invoke(null, null) as DXEntityTemplate;
        }

        public static void SetZombieRoamingDistance(DXEntity zombie, int cellsAway)
        {
            if (zombie == null || _propZombieCellsAwayRoaming == null) return;
            if (_typeZombie != null && _typeZombie.IsInstanceOfType(zombie))
                _propZombieCellsAwayRoaming.SetValue(zombie, cellsAway);
        }

        public static void AttachAutoMiniMapInfo(DXEntity entity)
        {
            if (entity == null || _typeCMiniMapInfo == null) return;
            try
            {
                if (!HasComponent(entity, _typeCMiniMapInfo))
                {
                    if (Activator.CreateInstance(_typeCMiniMapInfo) is DXComponent comp)
                        entity.AddComponent(comp);
                }
            }
            catch { }
        }

        public static float ColonyHours
        {
            get
            {
                try
                {
                    var ls = CurrentLevelState;
                    var prop = ls != null ? GetLevelProperty("ColonyMinutes") : null;
                    if (prop != null)
                        return Convert.ToSingle(prop.GetValue(ls)) / 60f;
                }
                catch { }
                return 0f;
            }
        }

        public static int GridNCells => DXLevel.Current?.NCells ?? 0;

        public static IList GetCurrentLevelEvents()
        {
            var fromState = GetLevelProperty("LevelEvents")?.GetValue(CurrentLevelState) as IList;
            if (fromState != null && fromState.Count > 0)
                return fromState;
            object ext = DXLevel.Current?.Extension;
            if (ext != null && _propLevelExtLevelEvents != null)
            {
                var fromExt = _propLevelExtLevelEvents.GetValue(ext) as IList;
                if (fromExt != null && fromExt.Count > 0)
                    return fromExt;
            }
            return fromState;
        }

        public static int GetLevelEventNTimesTriggered(object levelEvent)
        {
            if (levelEvent == null || _propLevelEventNTimesTriggered == null) return 0;
            return (int)_propLevelEventNTimesTriggered.GetValue(levelEvent);
        }

        public static bool IsLevelEventPeriodicSwarm(object levelEvent, out bool isFinalSwarm, out bool showCountdown, out bool attackCC, out int maxReps)
        {
            isFinalSwarm = false;
            showCountdown = false;
            attackCC = false;
            maxReps = 0;
            if (levelEvent == null) return false;
            isFinalSwarm = _propLevelEventFinalSwarm != null && (bool)_propLevelEventFinalSwarm.GetValue(levelEvent);
            showCountdown = _propLevelEventShowCountdown != null && (bool)_propLevelEventShowCountdown.GetValue(levelEvent);
            attackCC = _propLevelEventAttackCC != null && (bool)_propLevelEventAttackCC.GetValue(levelEvent);
            maxReps = _propLevelEventMaxRepetitions != null ? (int)_propLevelEventMaxRepetitions.GetValue(levelEvent) : 0;
            return attackCC && !isFinalSwarm;
        }

        public static void GetLevelEventRepetitionScale(object levelEvent, out float factorPerRep, out float maxFactor)
        {
            factorPerRep = _propLevelEventFactorUnitsPerRep != null ? (float)_propLevelEventFactorUnitsPerRep.GetValue(levelEvent) : 0f;
            maxFactor = _propLevelEventMaxFactorUnitsPerRep != null ? (float)_propLevelEventMaxFactorUnitsPerRep.GetValue(levelEvent) : 0f;
        }

        public static string GetLevelEventMessage(object levelEvent)
        {
            return _propLevelEventMessage?.GetValue(levelEvent) as string;
        }

        public static void SetLevelEventMessage(object levelEvent, string msg)
        {
            if (levelEvent != null && _propLevelEventMessage != null)
                _propLevelEventMessage.SetValue(levelEvent, msg);
        }

        public static string GetUnitGeneratorName(object unitGen)
        {
            if (unitGen == null || _propUnitGenConfig == null || _propUnitGenConfigName == null) return string.Empty;
            object cfg = _propUnitGenConfig.GetValue(unitGen);
            return cfg != null ? (_propUnitGenConfigName.GetValue(cfg) as string ?? string.Empty) : string.Empty;
        }

        public static IList GetAllEdgeUnitGenerators()
        {
            var game = DXGame.Current;
            if (game?.Entities == null || _typeCUnitGenerator == null) return null;
            var list = new List<object>();
            foreach (var ent in game.Entities)
            {
                if (ent == null || ent.Disposed) continue;
                var comp = FindComponent(ent, _typeCUnitGenerator);
                if (comp != null) list.Add(comp);
            }
            return list;
        }

        public static IList CreateTypedUnitGeneratorList(IEnumerable<object> items)
        {
            if (_typeCUnitGenerator == null) return null;
            var listType = typeof(List<>).MakeGenericType(_typeCUnitGenerator);
            var typedList = (IList)Activator.CreateInstance(listType);
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item != null && _typeCUnitGenerator.IsInstanceOfType(item))
                        typedList.Add(item);
                }
            }
            return typedList;
        }

        public static void SetLevelEventCurrentGenerators(object levelEvent, IList typedList)
        {
            if (levelEvent != null && _propLevelEventCurrentUnitGenerators != null)
                _propLevelEventCurrentUnitGenerators.SetValue(levelEvent, typedList);
        }

        public static ulong GetGenerationRequestEventId(object req)
        {
            return req != null && _propGenReqIDEvent != null ? (ulong)_propGenReqIDEvent.GetValue(req) : 0UL;
        }

        public static int GetGenerationRequestCount(object req)
        {
            return req != null && _propGenReqCount != null ? (int)_propGenReqCount.GetValue(req) : 0;
        }

        public static int GetGenerationRequestSpeed(object req)
        {
            return req != null && _propGenReqGenSpeed != null ? (int)_propGenReqGenSpeed.GetValue(req) : 6;
        }

        public static void SetGenerationRequestSpeed(object req, int speed)
        {
            if (req != null && _propGenReqGenSpeed != null)
                _propGenReqGenSpeed.SetValue(req, Math.Max(1, Math.Min(12, speed)));
        }

        public static DXEntityTemplate GetGenerationRequestTemplate(object req)
        {
            return req != null && _methodGenReqGetTemplate != null ? _methodGenReqGetTemplate.Invoke(req, null) as DXEntityTemplate : null;
        }

        private const string CcInvulnScriptVarKey = "TABCOOP_CC_INVULN";

        public static bool ReadCommandCenterInvulnerableFlagFromScriptVars()
        {
            try
            {
                var vars = GetLevelScriptVariables();
                if (vars == null || !vars.Contains(CcInvulnScriptVarKey)) return false;
                return string.Equals(vars[CcInvulnScriptVarKey]?.ToString(), "1", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        public static int SetCommandCenterInvulnerableFlag(bool enabled, bool persistInSave = true)
        {
            if (persistInSave)
            {
                var vars = GetLevelScriptVariables();
                if (vars != null)
                {
                    if (enabled) vars[CcInvulnScriptVarKey] = "1";
                    else vars.Remove(CcInvulnScriptVarKey);
                }
            }

            Testing.CommandCenterInvulnerability.RefreshFromCurrentLevel();
            Testing.CommandCenterInvulnerability.SetRuntimeOverride(enabled);

            int count = 0;
            var ccs = GetAllCommandCenters(aliveOnly: true);
            for (int i = 0; i < ccs.Count; i++)
            {
                var cc = ccs[i];
                if (cc == null || cc.Disposed) continue;
                count++;
                if (enabled) TryCorrectLife(cc, 9999999, forceKill: false);
            }
            return count;
        }

        public static bool IsLevelEventAllInfectedToCC(object levelEvent)
        {
            if (levelEvent == null || _propLevelEventAllInfectedToCC == null) return false;
            try { return (bool)_propLevelEventAllInfectedToCC.GetValue(levelEvent); }
            catch { return false; }
        }

        public static void PrewarmFinalSwarmMapZombiesParallel()
        {
            if (!Determinism.LockstepClock.IsDeterministicModeActive)
                return;
            var game = DXGame.Current;
            if (game?.Entities == null || _typeZombie == null) return;

            bool isMultiFaction = Sync.CoopSessionManager.IsMultiFactionActive
                || (Sync.FactionOwnershipTracker.IsInitialized && Sync.FactionOwnershipTracker.ActivePlayerCount >= 2);
            DXEntity sharedCc = isMultiFaction ? null : GetCommandCenterEntity();
            bool hasSharedCc = sharedCc != null && !sharedCc.Disposed;
            Point sharedCcCell = hasSharedCc ? sharedCc.Cell : Point.Empty;

            var batch = new List<Determinism.DeterministicPathfindingPatch.PathPrewarmItem>(4096);
            foreach (var ent in game.Entities)
            {
                if (ent == null || ent.Disposed) continue;
                if ((ent.ID & 0x8000000000000000UL) != 0UL) continue;
                if (!_typeZombie.IsInstanceOfType(ent)) continue;
                if (!GetUnitAttackCommandCenter(ent)) continue;

                Point endCell;
                if (isMultiFaction)
                {
                    var cc = Sync.FactionOwnershipTracker.PickTargetCommandCenterForZombie(ent, splitDistantById: true);
                    if (cc == null || cc.Disposed) continue;
                    endCell = cc.Cell;
                }
                else
                {
                    if (!hasSharedCc) continue;
                    endCell = sharedCcCell;
                }

                int cs = (int)Math.Ceiling(2f * ent.Radius);
                if (cs < 1) cs = 1;
                else if (cs > 3) cs = 3;

                batch.Add(new Determinism.DeterministicPathfindingPatch.PathPrewarmItem
                {
                    CellSize = cs,
                    Start = ent.Cell,
                    End = endCell,
                    Mask = GetEntityPathfindingMask(ent),
                    MaxNodes = null
                });
            }

            if (batch.Count >= 4)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Determinism.DeterministicPathfindingPatch.PrewarmUniquePathsParallel(batch);
                ModLogger.Info($"[Pathfinding] Prewarmed FinalSwarm map zombies in parallel: zombies={batch.Count}, elapsedMs={sw.ElapsedMilliseconds}.");
            }
        }

        public static int TriggerFinalSwarmEvents(out int queuedRequests, out int queuedUnits)
        {
            queuedRequests = 0;
            queuedUnits = 0;
            var events = GetCurrentLevelEvents();
            var tEvent = GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXLevelEvent);
            var mTrigger = tEvent != null ? AccessTools.Method(tEvent, ObfuscatedSymbols.Method_LevelEvent_Trigger, Type.EmptyTypes) : null;
            if (events == null || mTrigger == null) return 0;

            int triggered = 0;
            foreach (object ev in events)
            {
                if (ev == null) continue;
                IsLevelEventPeriodicSwarm(ev, out bool isFinalSwarm, out _, out _, out _);
                if (!isFinalSwarm) continue;
                mTrigger.Invoke(ev, null);
                triggered++;
            }

            var allEdgeGens = GetAllEdgeUnitGenerators();
            if (allEdgeGens != null && allEdgeGens.Count > 0)
            {
                var propReqs = AccessTools.Property(allEdgeGens[0].GetType(), "Requests");
                foreach (object g in allEdgeGens)
                {
                    if (propReqs?.GetValue(g) is IList reqs)
                    {
                        queuedRequests += reqs.Count;
                        foreach (object r in reqs)
                            queuedUnits += GetGenerationRequestCount(r);
                    }
                }
            }
            return triggered;
        }
    }
}
