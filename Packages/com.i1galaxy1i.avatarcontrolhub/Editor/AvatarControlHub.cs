#if UNITY_EDITOR
using DG.Tweening;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using static VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu;

namespace Avatar_Control_Hub
{
    public class BlendTreeWindow : EditorWindow
    {
        private const string ReorderDragTooltip = "Drag to reorder controls. Escape cancels the drag.";

        private static readonly Dictionary<string, AnimatorControllerParameterType> VrcBuiltInParameterTypes = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal)
        {
            { "IsLocal", AnimatorControllerParameterType.Bool },
            { "PreviewMode", AnimatorControllerParameterType.Int },
            { "Viseme", AnimatorControllerParameterType.Int },
            { "Voice", AnimatorControllerParameterType.Float },
            { "GestureLeft", AnimatorControllerParameterType.Int },
            { "GestureRight", AnimatorControllerParameterType.Int },
            { "GestureLeftWeight", AnimatorControllerParameterType.Float },
            { "GestureRightWeight", AnimatorControllerParameterType.Float },
            { "AngularY", AnimatorControllerParameterType.Float },
            { "VelocityX", AnimatorControllerParameterType.Float },
            { "VelocityY", AnimatorControllerParameterType.Float },
            { "VelocityZ", AnimatorControllerParameterType.Float },
            { "VelocityMagnitude", AnimatorControllerParameterType.Float },
            { "Upright", AnimatorControllerParameterType.Float },
            { "Grounded", AnimatorControllerParameterType.Bool },
            { "Seated", AnimatorControllerParameterType.Bool },
            { "AFK", AnimatorControllerParameterType.Bool },
            { "TrackingType", AnimatorControllerParameterType.Int },
            { "VRMode", AnimatorControllerParameterType.Int },
            { "MuteSelf", AnimatorControllerParameterType.Bool },
            { "InStation", AnimatorControllerParameterType.Bool },
            { "Earmuffs", AnimatorControllerParameterType.Bool },
            { "IsOnFriendsList", AnimatorControllerParameterType.Bool },
            { "AvatarVersion", AnimatorControllerParameterType.Int },
            { "IsAnimatorEnabled", AnimatorControllerParameterType.Bool },
            { "ScaleModified", AnimatorControllerParameterType.Bool },
            { "ScaleFactor", AnimatorControllerParameterType.Float },
            { "ScaleFactorInverse", AnimatorControllerParameterType.Float },
            { "EyeHeightAsMeters", AnimatorControllerParameterType.Float },
            { "EyeHeightAsPercent", AnimatorControllerParameterType.Float }
        };
        private static readonly HashSet<string> VrcBuiltInParameterNames = new HashSet<string>(VrcBuiltInParameterTypes.Keys, StringComparer.Ordinal);

        private UnityEngine.Object _targetAsset;
        private VRCExpressionParameters _vrcExpressionParametersAsset;
        private VRCExpressionsMenu _vrcExpressionMenuAsset;

        // Check menu references outside OnGUI, rebuilding the graph only when its assets change.
        [NonSerialized] private Dictionary<VRCExpressionsMenu, int> _menuParameterAssetVersions = new Dictionary<VRCExpressionsMenu, int>();
        [NonSerialized] private VRCExpressionsMenu _menuParameterSyncRoot;
        [NonSerialized] private VRCExpressionParameters _menuParameterSyncAsset;
        [NonSerialized] private bool _menuParameterSyncDirty = true;
        [NonSerialized] private double _nextMenuParameterSyncCheck;

        // Setup checks run on request; drawing the window only displays their cached results.
        private const string PrefShowSetupChecksKey = "BlendTree_ShowSetupChecks";
        private const string PrefSetupListHeightKey = "BlendTree_SetupListHeight";
        private const string PrefSetupPlatformKey = "BlendTree_SetupPlatform";
        private enum SetupPlatform { Automatic, PC, Mobile }
        private static readonly string[] SetupPlatformNames = { "Current Build Target", "PC", "Mobile" };
        // Local rules, checked against VRChat's documented mobile component limits (September 2026).
        // https://creators.vrchat.com/avatars/avatar-performance-ranking-system/#mobile-avatar-component-limits
        private const int SetupMobilePhysBoneLimit = 8;
        private const int SetupMobileTransformLimit = 64;
        private const int SetupMobileColliderLimit = 16;
        private const int SetupMobileCollisionCheckLimit = 64;
        private const int SetupMobileContactLimit = 16;
        private const int SetupMobileConstraintLimit = 150;
        private const int SetupMobileConstraintDepthLimit = 50;
        private const int SetupSyncedParameterBitLimit = 256;
        private static readonly HashSet<string> SetupMobileAvatarShaders = new HashSet<string>(StringComparer.Ordinal)
        {
            "VRChat/Mobile/Standard Lite", "VRChat/Mobile/Diffuse", "VRChat/Mobile/Bumped Diffuse",
            "VRChat/Mobile/Bumped Mapped Specular", "VRChat/Mobile/Toon Lit", "VRChat/Mobile/MatCap Lit",
            "VRChat/Mobile/Particles/Additive", "VRChat/Mobile/Particles/Multiply",
            "VRChat/Mobile/Toon Standard", "VRChat/Mobile/Toon Standard (Outline)"
        };
        // Retained only to migrate the avatar from older saved window layouts.
        [SerializeField] private VRCAvatarDescriptor _setupAvatar;
        private SetupPlatform _setupPlatform;
        private GameObject _setupAvatarAtScan;
        private bool _setupMobileAtScan;
        private bool _showSetupChecks = true;
        private bool _showSetupMissingParameters;
        private bool _hasSetupScan;
        private bool _setupChecksStale;
        private Vector2 _setupScrollPos;
        private float _setupListHeight = 150f;
        private bool _isResizingSetupList;
        private double _nextSetupFreshnessCheck;
        private UnityEngine.Object _setupTargetAtScan;
        private VRCExpressionParameters _setupParametersAtScan;
        private VRCExpressionsMenu _setupMenuAtScan;
        private readonly List<SetupIssue> _setupIssues = new List<SetupIssue>();
        private readonly Dictionary<UnityEngine.Object, int> _setupAssetVersions = new Dictionary<UnityEngine.Object, int>();

        private struct SetupIssue
        {
            public string message;
            public MessageType severity;
            public UnityEngine.Object asset;
            public GameObject[] avatarObjects;
            public Component[] avatarComponents;
            public Action autoFix;
            public List<ParameterUsageEntry> locations;
            public string locationId;
            public List<SetupMissingParameter> missingParameters;
            public List<SetupMissingScript> missingScripts;
            public bool writeDefaultsOptions;
        }

        private sealed class SetupMissingScript
        {
            public GameObject owner;
            public int componentIndex;
            public string objectPath;
            public int ownerVersion;
            public int[] componentIds;
        }

        [Flags]
        private enum SetupParameterTypes { None = 0, Bool = 1, Int = 2, Float = 4, Numeric = Int | Float, All = Bool | Numeric }

        private sealed class SetupMissingParameter
        {
            public string name;
            public SetupParameterTypes allowedTypes = SetupParameterTypes.All;
            public SetupParameterTypes valueHints;
            public AnimatorControllerParameterType? type;
            public bool typeChosen;
            public bool missingFromAnimator;
            public string typeHint;
            public readonly List<ParameterUsageEntry> locations = new List<ParameterUsageEntry>();
        }

        private List<BlendTree> _rootTrees = new List<BlendTree>();
        private List<BlendTree> _navigationPath = new List<BlendTree>();

        private string _searchQuery = "";
        private Vector2 _rootScrollPos;
        private Vector2 _childScrollPos;
        private Vector2 _paramScrollPos;
        private Vector2 _mainScrollPos;

        private const float ElementHeight = 24f;

        // Copy replaces the clipboard; Cut adds/removes rows from a pending move batch.
        private static readonly List<ChildMotion> _motionClipboard = new List<ChildMotion>();
        private static readonly List<MotionCutEntry> _motionCuts = new List<MotionCutEntry>();
        private sealed class MotionCutEntry
        {
            public BlendTree owner;
            public int index;
            public ChildMotion original;
            public bool invalid;
        }

        // Drag and drop state for blend tree child motions
        private int _dragSourceIndex = -1;
        private bool _isDragging = false;
        private BlendTree _motionDragTree;
        private ChildMotion[] _motionDragOriginalChildren;
        private bool _motionDragHasMoved;
        private Dictionary<MotionCutEntry, int> _motionDragOriginalCutIndices;

        // Drag and drop state for parameters
        private int _paramDragSourceIndex = -1;
        private bool _isParamDragging = false;
        private AnimatorController _paramDragController;
        private AnimatorControllerParameter[] _paramDragOriginalParameters;
        private int _paramDragOriginalSelection = -1;
        private bool _paramDragHasMoved;
        private string _paramSearchQuery = ""; // Search bar state for parameters

        // Resizer state for parameters list
        private float _paramListHeight = 150f;
        private bool _isResizingParamList = false;

        // Pref keys for saving asset paths, converter folder, and options
        private const string PrefLastAssetKey = "BlendTree_LastAssetPath";
        private const string PrefExpressionParametersKey = "BlendTree_ExpressionParametersPath";
        private const string PrefExpressionMenuKey = "BlendTree_ExpressionMenuPath";
        private const string PrefAvatarRootKey = "BlendTree_AvatarRootGlobalId";
        private const string PrefConverterFolderKey = "BlendTree_ConverterFolderPath";
        private const string PrefConverterAutoFolderKey = "BlendTree_ConverterAutoFolder";
        private const string PrefConverterToggleModeKey = "BlendTree_ConverterToggleMode";
        private const string PrefParamAddPositionKey = "BlendTree_ParamAddPosition";
        private const string PrefParamListHeightKey = "BlendTree_ParamListHeight";
        private const string PrefShowParamManagerKey = "BlendTree_ShowParamManager";
        private const string PrefShowBlendTreeEditorKey = "BlendTree_ShowBlendTreeEditor";
        private const string PrefHideVrcBuiltInParametersKey = "BlendTree_HideVrcBuiltInParameters";
        private const string PrefParameterDefaultWarningKey = "BlendTree_ParameterDefaultWarning";
        private const string PrefParameterDuplicateWarningKey = "BlendTree_ParameterDuplicateWarning";

        private static string GetProjectScopedPreferenceKey(string key)
        {
            string projectPath = System.IO.Path.GetFullPath(Application.dataPath)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(projectPath));
                return key + "_" + BitConverter.ToString(hash).Replace("-", "");
            }
        }

        // --- Parameter Add Position State ---
        private enum AddPosition { BelowSelected, AboveSelected }
        private AddPosition _paramAddPosition = AddPosition.BelowSelected;
        private int _selectedParamIndex = -1;

        // --- Animation Converter State ---
        private AnimationClip _converterSourceClip;
        private bool _showConverter = false;
        private string _converterSaveFolderPath = "Assets";
        private bool _converterSaveAutoFolderPath = true;
        private string _converterParameter = "BlendTreeWeight";
        private bool _converterToggleMode = false;
        private bool _showParamManager = true;
        private bool _showBlendTreeEditor = true;
        private const string PrefShowToggleMakerKey = "BlendTree_ShowToggleMaker";
        private bool _showToggleMaker = true;
        [SerializeField] private AnimationClip _AnimationRecorderClip;
        // Recording belongs to the editor session, so closing the Hub cannot interrupt its cleanup.
        private static bool _toggleRecording;
        private static GameObject _toggleRecordingAvatar;
        private static GameObject _toggleRecordingCopy;
        private static bool _toggleAvatarWasActive;
        private static ToggleMakerPose _toggleRecordingPose;
        private static AnimationClip _toggleRecordingClip;
        private static AnimationWindow _toggleRecordingWindow;
        private static UnityEngine.Object _togglePreviousWindowSelection;
        private static UnityEngine.Object[] _togglePreviousSelection;
        private static object _toggleRecordingWindowState;
        private static readonly Dictionary<GameObject, ToggleMakerPose> _toggleHiddenAvatars = new Dictionary<GameObject, ToggleMakerPose>();
        private static readonly List<UnityEngine.Object> _toggleRecordingTemporaryObjects = new List<UnityEngine.Object>();
        private static readonly List<RecordingHubWindowState> _recordingClosedHubWindows = new List<RecordingHubWindowState>();
        private static Func<bool> _isAnimationRecording;
        private static bool _hubObservedRecording;
        private static bool _recordingEditorQuitting;
        private const System.Reflection.BindingFlags ToggleRecorderFlags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        private bool _hideVrcBuiltInParameters = true;
        private bool _showParameterDefaultWarning = true;
        private bool _showParameterDuplicateWarning = true;

        // Animation tools share the loaded controller and an optional extra clip selection.
        private const string PrefShowBlendshapeMapperKey = "BlendTree_ShowBlendshapeMapper";
        private const string PrefShowPathReplaceEditorKey = "BlendTree_ShowPathReplaceEditor";
        private const string PrefShowAnimationMergerKey = "BlendTree_ShowAnimationMerger";
        private const string PrefShowAnimatorLayerMergerKey = "BlendTree_ShowAnimatorLayerMerger";
        private const string PrefLayerMergerFoldoutKey = "BlendTree_LayerMergerFoldout";
        private const string PrefControllerMergerFoldoutKey = "BlendTree_ControllerMergerFoldout";
        private const string PrefParameterMergerFoldoutKey = "BlendTree_ParameterMergerFoldout";
        private const string PrefParameterUsageFollowSelectionKey = "BlendTree_ParameterUsageFollowSelection";
        private const string PrefParameterUsageFilterKey = "BlendTree_ParameterUsageFilter";
        private const string PrefParameterUsageSortKey = "BlendTree_ParameterUsageSort";
        private const int ParameterUsageToolIndex = 4;
        private const string PrefShowFxAnimationClipsKey = "BlendTree_ShowFxAnimationClips";
        private static readonly string[] AnimationToolHeightKeys =
        {
            "BlendTree_BlendshapeMapperHeight", "BlendTree_PathReplaceHeight", "BlendTree_AnimationMergerHeight",
            "BlendTree_AnimatorLayerMergerHeight", "BlendTree_ParameterUsageHeight"
        };
        private bool _showBlendshapeMapper = true;
        private bool _showPathReplaceEditor = true;
        private bool _showAnimationMerger = true;
        private bool _showAnimatorLayerMerger = true;
        [NonSerialized] private bool _showParameterUsage;
        private string _parameterUsageName = "";
        private bool _parameterUsageFollowSelection;
        private string _parameterUsageFollowedName;
        private AnimatorController _parameterUsageFollowedController;
        private string _parameterUsageScannedName;
        private AnimatorController _parameterUsageController;
        private GameObject _parameterUsageRoot;
        private VRCExpressionsMenu _parameterUsageMenu;
        private VRCExpressionParameters _parameterUsageParameters;
        private readonly List<ParameterUsageEntry> _parameterUsages = new List<ParameterUsageEntry>();
        private readonly List<ParameterUsageEntry> _visibleParameterUsages = new List<ParameterUsageEntry>();
        private bool _parameterUsageViewDirty = true;
        [NonSerialized] private bool _showParameterManagerMissingParameters;
        private Vector2 _parameterManagerMissingScroll;
        [NonSerialized] private UnusedParameterRemovalPlan _unusedParameterRemoval;
        [NonSerialized] private string _unusedParameterRemovalError;
        private Vector2 _unusedParameterRemovalScroll;
        private sealed class UnusedParameterRemovalRow
        {
            public string name;
            public string animatorTypes;
            public string vrcTypes;
            public int animatorCount;
            public int vrcCount;
        }
        private sealed class UnusedParameterRemovalPlan
        {
            public AnimatorController controller;
            public VRCExpressionParameters vrcAsset;
            public readonly List<UnusedParameterRemovalRow> rows = new List<UnusedParameterRemovalRow>();
            public int animatorCount;
            public int vrcCount;
        }
        private enum ParameterUsageKind { AnimatorDefinition, VrcDefinition, Transition, BlendTree, StateSettings, Driver, Animation, Menu, Contact, PhysBone }
        private static readonly string[] ParameterUsageKindLabels =
        {
            "Animator Definitions", "VRC Definitions", "Transitions", "BlendTrees", "State Settings",
            "Parameter Drivers", "Animation Clips", "Menus", "Contacts", "PhysBones"
        };
        private static readonly string[] ParameterUsageSortLabels = { "Scan Order", "Usage Type", "Location" };
        private int _parameterUsageFilter = -1;
        private int _parameterUsageSort;
        private int _parameterUsageAnimatorNavigation;
        private sealed class ParameterUsageEntry
        {
            public ParameterUsageKind kind;
            public string location;
            public UnityEngine.Object target;
            public AnimatorController controller;
            public int layer = -1;
            public string layerName;
            public AnimatorStateMachine layerRoot;
            public AnimatorStateMachine machine;
            public AnimatorState state;
            public List<VRCExpressionsMenu> menuPath;
            public VRCExpressionsMenu.Control menuControl;
            // Used by Avatar Setup to group missing names and infer a compatible Animator type.
            public string missingParameterName;
            public SetupParameterTypes missingParameterTypes = SetupParameterTypes.All;
            public float? missingParameterValue;
        }
        private const float AnimationToolClipListMinimumHeight = 80f;
        private const float AnimationToolEmptyListMinimumHeight = 28f;
        // Initial estimates are replaced by the actual control heights after layout.
        private readonly float[] _animationToolControlsHeights = { 260f, 340f, 300f };
        private readonly float[] _animationToolHeights = { 360f, 440f, 400f, 320f, 220f };
        private readonly Vector2[] _animationToolScroll = new Vector2[5];
        private int _animationToolResizeIndex = -1;
        private int _animationToolResizeControlId;
        private UnityEngine.Object _animationToolSourceAsset;
        private bool _animationToolClipsDirty = true;
        private double _nextAnimationToolClipRefresh;
        private readonly List<AnimationClip> _animationToolClips = new List<AnimationClip>();
        private readonly List<AnimationClip> _animationToolExtraClips = new List<AnimationClip>();
        private readonly HashSet<AnimationClip> _fxAnimationToolExcludedClips = new HashSet<AnimationClip>();
        private readonly HashSet<AnimationClip> _manualAnimationToolExcludedClips = new HashSet<AnimationClip>();
        private readonly Vector2[] _manualAnimationToolScroll = new Vector2[3];
        private readonly Vector2[] _fxAnimationToolScroll = new Vector2[3];
        private bool _showFxAnimationClips;
        [UnityEngine.Serialization.FormerlySerializedAs("_animationToolsAvatarRoot")]
        [SerializeField] private GameObject _avatarRoot;
        private string _avatarRootPreferenceKey;
        private bool _mapperRemoveOldPaths;
        private bool _mapperCreateNewFiles;
        private string _pathSearch = "";
        private string _pathReplacement = "";
        private GameObject _pathSearchObject;
        private GameObject _pathReplacementObject;
        private bool _pathSearchExcludeRoot = true;
        private bool _pathReplacementExcludeRoot = true;
        private bool _pathExactMatch = true;
        private bool _pathCreateNewFiles;
        private AnimationClip _mergerBaseClip;
        private bool _mergerCreateNewFile;

        private AnimatorController _layerMergerController;
        private AnimatorStateMachine _layerMergeTarget;
        private readonly HashSet<AnimatorStateMachine> _layerMergeSources = new HashSet<AnimatorStateMachine>();
        private string _layerMergeSharedName = "";
        private bool _layerMergeRemoveSources;
        private bool _layerMergerFoldout = true;
        private bool _controllerMergerFoldout = true;
        [SerializeField] private List<AnimatorController> _controllerMergeSources = new List<AnimatorController>();
        [NonSerialized] private Rect _mergerViewportScreen;
        [NonSerialized] private ControllerMergePlan _controllerMergePlan;
        private readonly Dictionary<AnimatorController, int> _controllerMergeVersions = new Dictionary<AnimatorController, int>();

        private sealed class ControllerMergeParameter
        {
            public AnimatorControllerParameter original;
            public AnimatorControllerParameterType type;
            public readonly Dictionary<AnimatorControllerParameterType, string> owners = new Dictionary<AnimatorControllerParameterType, string>();
        }

        private sealed class ControllerMergePlan
        {
            public AnimatorController target;
            public readonly List<AnimatorController> sources = new List<AnimatorController>();
            public readonly List<ControllerMergeParameter> parameters = new List<ControllerMergeParameter>();
            public readonly Dictionary<string, ControllerMergeParameter> byName = new Dictionary<string, ControllerMergeParameter>(StringComparer.Ordinal);
            public readonly List<string> errors = new List<string>();
            public readonly List<string> promotions = new List<string>();
            public int layerCount;
        }

        private bool _parameterMergerFoldout = true;
        [SerializeField] private List<VRCExpressionParameters> _parameterMergeSources = new List<VRCExpressionParameters>();
        [NonSerialized] private VrcParameterMergePlan _parameterMergePlan;
        private readonly Dictionary<VRCExpressionParameters, int> _parameterMergeVersions = new Dictionary<VRCExpressionParameters, int>();

        private sealed class VrcParameterMergeEntry
        {
            public VRCExpressionParameters.Parameter original;
            public VRCExpressionParameters.ValueType type;
            public readonly Dictionary<VRCExpressionParameters.ValueType, string> owners = new Dictionary<VRCExpressionParameters.ValueType, string>();
        }

        private sealed class VrcParameterMergePlan
        {
            public VRCExpressionParameters target;
            public readonly List<VRCExpressionParameters> sources = new List<VRCExpressionParameters>();
            public readonly List<VrcParameterMergeEntry> parameters = new List<VrcParameterMergeEntry>();
            public readonly Dictionary<string, VrcParameterMergeEntry> byName = new Dictionary<string, VrcParameterMergeEntry>(StringComparer.Ordinal);
            public readonly List<string> errors = new List<string>();
            public readonly List<string> promotions = new List<string>();
            public int addedCount, syncedBits;
        }

        // --- VRC Menu Editor State ---
        private bool _showMenuEditor = true;
        private const string PrefShowMenuEditorKey = "BlendTree_ShowMenuEditor";
        private int _selectedMenuControlIndex = -1;
        private Vector2 _menuScrollPos;
        private string _newSubMenuName = "New SubMenu";
        private int _menuDragSourceIndex = -1;
        private bool _isMenuDragging = false;
        private VRCExpressionsMenu _menuDragOwner;
        private VRCExpressionsMenu.Control[] _menuDragOriginalControls;
        private bool _menuDragHasMoved;
        private int _menuDragControlId;
        private float _menuDragPointerOffset;
        private float _menuListHeight = 250f;
        private bool _isResizingMenuList = false;
        private const string PrefMenuListHeightKey = "BlendTree_MenuListHeight";
        private string _menuSearchQuery = "";
        private const string PrefMenuSearchAllKey = "BlendTree_MenuSearchAll";
        private const string PrefMenuTypeFilterKey = "BlendTree_MenuTypeFilter";
        private bool _menuSearchAll = true;
        private int _menuTypeFilter = -1;
        private int _menuSearchCachedType = -1;
        private VRCExpressionsMenu _menuEditorRoot;
        private VRCExpressionsMenu _selectedMenuOwner;
        private VRCExpressionsMenu.Control _selectedMenuControlReference;
        private int _menuDropTarget = -1;
        private Rect _menuViewportRect;
        private readonly List<Rect> _menuRowRects = new List<Rect>();
        private readonly List<int> _menuRowIndices = new List<int>();
        private readonly List<MenuControlEntry> _menuSearchResults = new List<MenuControlEntry>();
        private VRCExpressionsMenu _menuSearchRoot;
        private string _menuSearchText;
        private double _nextMenuSearchRefresh;
        [NonSerialized] private GUIStyle _menuControlNameStyle;
        [NonSerialized] private GUIStyle _menuMetadataStyle;
        [NonSerialized] private bool _menuStylesProSkin;
        private string _menuNameColourControl;
        private int _menuNameColourStart;
        private int _menuNameColourLength = -1;
        private string _menuNameClearColourControl;
        private string _menuNameFormatControl;
        private string _menuNameFormatKey;
        private string _menuNameFormatValue;
        private bool _menuNameFormatRemove;
        private string _menuNameRawControl;
        private string _menuNameSelectAllOnClickControl;
        private static readonly System.Text.RegularExpressions.Regex MenuNameTagPattern =
            new System.Text.RegularExpressions.Regex(@"<\s*(?:(?<hex>#[0-9a-f]{3,8})|(?<close>/)?\s*(?<name>[a-z][a-z0-9-]*)(?<args>[^<>]*))\s*>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        private static readonly HashSet<string> MenuNameStyleTags = new HashSet<string>(StringComparer.Ordinal)
        {
            "b", "i", "u", "s", "size", "mark", "sub", "sup", "lowercase", "uppercase", "allcaps", "smallcaps",
            "font", "font-weight", "material", "gradient", "style", "link", "a", "nobr", "noparse", "align", "width",
            "cspace", "mspace", "indent", "line-indent", "line-height", "margin", "pos", "voffset", "rotate", "scale",
            "action", "liga", "frac"
        };
        private static readonly HashSet<string> MenuNameStandaloneTags = new HashSet<string>(StringComparer.Ordinal)
        {
            "alpha", "space", "page", "br", "sprite", "quad", "margin-left", "margin-right", "class", "zwsp", "nbsp", "shy"
        };
        private static readonly HashSet<string> MenuNameTextStyleTags = new HashSet<string>(StringComparer.Ordinal)
        {
            "b", "i", "u", "s", "size", "mark", "sub", "sup", "lowercase", "uppercase", "allcaps", "smallcaps",
            "font", "font-weight", "material", "gradient", "style"
        };

        private sealed class MenuNameFormat
        {
            public string key;
            public string opening;
            public string closing;
        }

        private sealed class MenuNameMarker
        {
            public int position;
            public string raw;
            public string colour;
            public MenuNameFormat[] formats;
        }

        private sealed class MenuNameColours
        {
            public string text = "";
            public readonly List<string> colours = new List<string>();
            public string emptyColour;
            public readonly List<MenuNameFormat[]> formats = new List<MenuNameFormat[]>();
            public MenuNameFormat[] emptyFormats = Array.Empty<MenuNameFormat>();
            public readonly List<MenuNameMarker> markers = new List<MenuNameMarker>();
        }

        private struct MenuControlEntry
        {
            public VRCExpressionsMenu owner;
            public VRCExpressionsMenu.Control control;
            public int index;
            public string path;
        }

        // --- Layer Editor State ---
        private bool _showLayerEditor = true;
        private const string PrefShowLayerEditorKey = "BlendTree_ShowLayerEditor";
        private int _selectedLayerIndex = -1;
        private Vector2 _layerScrollPos;
        private int _layerDragSourceIndex = -1;
        private bool _isLayerDragging = false;
        private int _layerDropTarget = -1;
        private int _layerDragStartIndex = -1;
        private int _layerDragControlId;
        private bool _layerDragHasMoved;
        private AnimatorController _layerEditorController;
        private AnimatorStateMachine _layerSelectionStateMachine;
        private string _layerSelectionName;
        private AnimatorController _layerSummaryController;
        private double _nextLayerSummaryRefresh;
        private readonly List<LayerSummary> _layerSummaries = new List<LayerSummary>();
        private readonly Dictionary<AnimatorState, bool> _layerStateParameterWarnings = new Dictionary<AnimatorState, bool>();
        private readonly List<Rect> _layerRowRects = new List<Rect>();
        private readonly List<int> _layerRowIndices = new List<int>();
        private Rect _layerViewportRect;
        private Rect _layerViewportScreenRect;

        private class LayerSummary
        {
            public int stateCount;
            public bool writeDefaultsWarning;
            public bool parameterWarning;
            public bool hasLayerControls;
            public string searchText;
        }
        private readonly List<AnimatorStateMachine> _stateMachineNavigation = new List<AnimatorStateMachine>();
        private Vector2 _stateBreadcrumbScrollPosition;
        private Vector2 _stateBrowserScrollPosition;
        private AnimatorStateMachine _stateSearchRoot;
        private string _stateSearchText;
        private double _nextStateSearchRefresh;
        private readonly List<LayerStateEntry> _cachedStateSearch = new List<LayerStateEntry>();
        private readonly List<LayerMachineEntry> _cachedMachineSearch = new List<LayerMachineEntry>();
        private AnimatorStateMachine _incomingTransitionRoot;
        private AnimatorState _incomingTransitionTarget;
        private double _nextIncomingTransitionRefresh;
        private readonly List<IncomingTransitionInfo> _cachedIncomingTransitions = new List<IncomingTransitionInfo>();

        private struct LayerStateEntry
        {
            public AnimatorStateMachine owner;
            public AnimatorState state;
            public string path;
        }

        private struct LayerMachineEntry
        {
            public AnimatorStateMachine machine;
            public string path;
        }

        private float _layerListHeight = 200f;
        private bool _isResizingLayerList = false;
        private const string PrefLayerListHeightKey = "BlendTree_LayerListHeight";
        private float _blendTreeListHeight = 250f;
        private bool _isResizingBlendTreeList = false;
        private const string PrefBlendTreeListHeightKey = "BlendTree_ListHeight";
        private Motion _layerMotionToAdd;
        private AnimatorState _selectedState = null;
        private AnimatorStateTransition _selectedTransition = null;
        private Editor _cachedBehaviourEditor = null;
        private StateMachineBehaviour _expandedLayerBehaviour;
        private string _layerSearchQuery = "";
        private string _stateSearchQuery = "";
        private AnimatorStateTransition _selectedIncomingTransition = null;
        private bool _showIncomingTransitions = false;
        private List<VRCExpressionsMenu> _menuNavigationPath = new List<VRCExpressionsMenu>();

        // Menu controls retain their source reference until the whole cut batch is pasted.
        private static readonly List<VRCExpressionsMenu.Control> _menuClipboard = new List<VRCExpressionsMenu.Control>();
        private static readonly List<MenuCutEntry> _menuCuts = new List<MenuCutEntry>();
        private sealed class MenuCutEntry
        {
            public VRCExpressionsMenu owner;
            public VRCExpressionsMenu.Control control;
        }

        private VRCExpressionsMenu.Control CloneMenuControl(VRCExpressionsMenu.Control source)
        {
            if (source == null) return null;

            var clone = new VRCExpressionsMenu.Control
            {
                name = source.name,
                icon = source.icon,
                type = source.type,
                style = source.style,
                value = source.value,
                subMenu = source.subMenu
            };

            if (source.parameter != null)
            {
                clone.parameter = new VRCExpressionsMenu.Control.Parameter { name = source.parameter.name };
            }

            if (source.subParameters != null)
            {
                clone.subParameters = new VRCExpressionsMenu.Control.Parameter[source.subParameters.Length];
                for (int i = 0; i < source.subParameters.Length; i++)
                {
                    clone.subParameters[i] = new VRCExpressionsMenu.Control.Parameter { name = source.subParameters[i]?.name };
                }
            }

            if (source.labels != null)
            {
                clone.labels = (VRCExpressionsMenu.Control.Label[])source.labels.Clone();
            }
            return clone;
        }

        private VRCExpressionsMenu CurrentMenu
        {
            get
            {
                if (_vrcExpressionMenuAsset == null)
                {
                    _menuNavigationPath.Clear();
                    return null;
                }

                if (_menuNavigationPath.Count == 0 || _menuNavigationPath[0] != _vrcExpressionMenuAsset)
                {
                    _menuNavigationPath.Clear();
                    _menuNavigationPath.Add(_vrcExpressionMenuAsset);
                }

                for (int i = 1; i < _menuNavigationPath.Count; i++)
                {
                    var parent = _menuNavigationPath[i - 1];
                    var child = _menuNavigationPath[i];
                    bool linked = child != null && parent != null && parent.controls != null && parent.controls.Exists(
                        control => control != null && control.type == Control.ControlType.SubMenu && control.subMenu == child);
                    if (!linked)
                    {
                        CancelMenuDrag();
                        _menuNavigationPath.RemoveRange(i, _menuNavigationPath.Count - i);
                        ResetMenuControlSelection();
                        break;
                    }
                }

                return _menuNavigationPath.Count > 0 ? _menuNavigationPath[_menuNavigationPath.Count - 1] : null;
            }
        }

        private BlendTree CurrentTree
        {
            get
            {
                for (int i = _navigationPath.Count - 1; i >= 0; i--)
                {
                    if (_navigationPath[i] == null)
                    {
                        _navigationPath.RemoveRange(i, _navigationPath.Count - i);
                        break;
                    }
                }
                return _navigationPath.Count > 0 ? _navigationPath[_navigationPath.Count - 1] : null;
            }
        }

        [MenuItem("I1Galaxy1I's Prefabs/Utilities/Avatar Control Hub")]
        public static void ShowWindow()
        {
            var window = GetWindow<BlendTreeWindow>("Avatar Control Hub - V1.6");
            window.minSize = new Vector2(600, 450);
        }

        private void OnEnable()
        {
            // Rebuild native GUI styles in OnGUI after reopening or reloading scripts.
            _menuControlNameStyle = null;
            _menuMetadataStyle = null;
            _hubDropdownPopupStyle = null;
            // Unity can restore cache keys during a script reload without restoring the cached rows.
            _parameterListInitialized = false;
            _parameterListControllerVersion = _parameterListVrcVersion = -1;
            _parameterRenderVersion = -1;
            _parameterPinnedRows = 0;
            _parameterPinnedKeyboardControl = _parameterPinnedHotControl = 0;
            _hasParamCache = false;
            titleContent = new GUIContent("Avatar Control Hub - V1.6");
            if (_avatarRoot == null && _setupAvatar != null) _avatarRoot = _setupAvatar.gameObject;
            _setupAvatar = null;
            _avatarRootPreferenceKey = GetProjectScopedPreferenceKey(PrefAvatarRootKey);
            LoadProjectAvatarRoot();
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened -= OnAvatarRootSceneOpened;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += OnAvatarRootSceneOpened;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved -= OnAvatarRootSceneSaved;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved += OnAvatarRootSceneSaved;
            EditorApplication.playModeStateChanged -= OnAvatarRootPlayModeChanged;
            EditorApplication.playModeStateChanged += OnAvatarRootPlayModeChanged;
            EditorApplication.delayCall -= RestoreProjectAvatarRoot;
            EditorApplication.delayCall += RestoreProjectAvatarRoot;
            Undo.undoRedoPerformed -= OnHubUndoRedo;
            Undo.undoRedoPerformed += OnHubUndoRedo;
            _showSetupChecks = EditorPrefs.GetBool(PrefShowSetupChecksKey, true);
            _setupListHeight = Mathf.Clamp(EditorPrefs.GetFloat(PrefSetupListHeightKey, 150f), 60f, 1000f);
            _setupPlatform = (SetupPlatform)Mathf.Clamp(EditorPrefs.GetInt(GetProjectScopedPreferenceKey(PrefSetupPlatformKey), 0), 0, 2);
            string lastAssetPreferenceKey = GetProjectScopedPreferenceKey(PrefLastAssetKey);
            string lastAssetPath = EditorPrefs.GetString(lastAssetPreferenceKey, "");
            if (!string.IsNullOrEmpty(lastAssetPath))
            {
                _targetAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(lastAssetPath);
                if (_targetAsset != null)
                {
                    RefreshAvailableTrees();
                }
                else
                {
                    EditorPrefs.DeleteKey(lastAssetPreferenceKey);
                }
            }

            string converterFolderPreferenceKey = GetProjectScopedPreferenceKey(PrefConverterFolderKey);
            if (!EditorPrefs.HasKey(converterFolderPreferenceKey))
            {
                // Migrate the old shared setting once, only if the folder exists in this project.
                string previousFolder = EditorPrefs.GetString(PrefConverterFolderKey, "Assets")
                    .Replace('\\', '/').TrimEnd('/');
                if ((previousFolder != "Assets" && !previousFolder.StartsWith("Assets/", StringComparison.Ordinal)) ||
                    !AssetDatabase.IsValidFolder(previousFolder))
                    previousFolder = "Assets";
                EditorPrefs.SetString(converterFolderPreferenceKey, previousFolder);
            }
            _converterSaveFolderPath = EditorPrefs.GetString(converterFolderPreferenceKey, "Assets");
            _converterSaveAutoFolderPath = EditorPrefs.GetBool(GetProjectScopedPreferenceKey(PrefConverterAutoFolderKey), true);
            _converterToggleMode = EditorPrefs.GetBool(PrefConverterToggleModeKey, false);
            _paramAddPosition = (AddPosition)EditorPrefs.GetInt(PrefParamAddPositionKey, (int)AddPosition.BelowSelected);
            _paramListHeight = EditorPrefs.GetFloat(PrefParamListHeightKey, 150f);

            _showParamManager = EditorPrefs.GetBool(PrefShowParamManagerKey, true);
            _showBlendTreeEditor = EditorPrefs.GetBool(PrefShowBlendTreeEditorKey, true);
            _showToggleMaker = EditorPrefs.GetBool(PrefShowToggleMakerKey, true);

            _showMenuEditor = EditorPrefs.GetBool(PrefShowMenuEditorKey, true);
            _menuSearchAll = EditorPrefs.GetBool(PrefMenuSearchAllKey, true);
            _menuTypeFilter = EditorPrefs.GetInt(PrefMenuTypeFilterKey, -1);
            if (!Enum.IsDefined(typeof(Control.ControlType), _menuTypeFilter)) _menuTypeFilter = -1;
            _menuListHeight = EditorPrefs.GetFloat(PrefMenuListHeightKey, 250f);

            _showLayerEditor = EditorPrefs.GetBool(PrefShowLayerEditorKey, true);
            _layerListHeight = EditorPrefs.GetFloat(PrefLayerListHeightKey, 200f);

            _showBlendshapeMapper = EditorPrefs.GetBool(PrefShowBlendshapeMapperKey, true);
            _showPathReplaceEditor = EditorPrefs.GetBool(PrefShowPathReplaceEditorKey, true);
            _showAnimationMerger = EditorPrefs.GetBool(PrefShowAnimationMergerKey, true);
            _showAnimatorLayerMerger = EditorPrefs.GetBool(PrefShowAnimatorLayerMergerKey, true);
            _layerMergerFoldout = EditorPrefs.GetBool(PrefLayerMergerFoldoutKey, true);
            _controllerMergerFoldout = EditorPrefs.GetBool(PrefControllerMergerFoldoutKey, true);
            if (_controllerMergeSources == null) _controllerMergeSources = new List<AnimatorController>();
            _controllerMergePlan = null;
            _parameterMergerFoldout = EditorPrefs.GetBool(PrefParameterMergerFoldoutKey, true);
            if (_parameterMergeSources == null) _parameterMergeSources = new List<VRCExpressionParameters>();
            _parameterMergePlan = null;
            // Tools panels are temporary and always start closed when the window opens.
            _showParameterUsage = false;
            _showParameterManagerMissingParameters = false;
            _unusedParameterRemoval = null;
            _unusedParameterRemovalError = null;
            _parameterUsageFollowSelection = EditorPrefs.GetBool(PrefParameterUsageFollowSelectionKey, false);
            _parameterUsageFilter = EditorPrefs.GetInt(PrefParameterUsageFilterKey, -1);
            _parameterUsageSort = Mathf.Clamp(EditorPrefs.GetInt(PrefParameterUsageSortKey, 0), 0, ParameterUsageSortLabels.Length - 1);
            _showFxAnimationClips = EditorPrefs.GetBool(GetProjectScopedPreferenceKey(PrefShowFxAnimationClipsKey), false);
            for (int i = 0; i < _animationToolHeights.Length; i++)
                _animationToolHeights[i] = Mathf.Clamp(EditorPrefs.GetFloat(AnimationToolHeightKeys[i], _animationToolHeights[i]), 80f, 1200f);
            _animationToolClipsDirty = true;

            _blendTreeListHeight = EditorPrefs.GetFloat(PrefBlendTreeListHeightKey, 250f);

            _hideVrcBuiltInParameters = EditorPrefs.GetBool(
                GetProjectScopedPreferenceKey(PrefHideVrcBuiltInParametersKey), true);
            _showParameterDefaultWarning = EditorPrefs.GetBool(
                GetProjectScopedPreferenceKey(PrefParameterDefaultWarningKey), true);
            _showParameterDuplicateWarning = EditorPrefs.GetBool(
                GetProjectScopedPreferenceKey(PrefParameterDuplicateWarningKey), true);

            string expressionParametersPreferenceKey = GetProjectScopedPreferenceKey(PrefExpressionParametersKey);
            string lastParamsPath = EditorPrefs.GetString(expressionParametersPreferenceKey, "");
            if (!string.IsNullOrEmpty(lastParamsPath))
            {
                _vrcExpressionParametersAsset = AssetDatabase.LoadAssetAtPath<VRCExpressionParameters>(lastParamsPath);
                if (_vrcExpressionParametersAsset == null) EditorPrefs.DeleteKey(expressionParametersPreferenceKey);
            }

            RemoveAnimatorOnlyParametersFromVrc();
            if (_hideVrcBuiltInParameters)
            {
                EnsureBlendTreeWeightFloatParameter(GetAnimatorController());
            }

            string expressionMenuPreferenceKey = GetProjectScopedPreferenceKey(PrefExpressionMenuKey);
            string lastMenuPath = EditorPrefs.GetString(expressionMenuPreferenceKey, "");
            if (!string.IsNullOrEmpty(lastMenuPath))
            {
                _vrcExpressionMenuAsset = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(lastMenuPath);
                if (_vrcExpressionMenuAsset == null) EditorPrefs.DeleteKey(expressionMenuPreferenceKey);
            }
            _menuParameterSyncDirty = true;
            _nextMenuParameterSyncCheck = 0;
            EditorApplication.update -= UpdateMenuParameterReferences;
            EditorApplication.update += UpdateMenuParameterReferences;
        }

        private void OnDisable()
        {
            EditorApplication.update -= UpdateMenuParameterReferences;
            _menuParameterAssetVersions.Clear();
            _menuParameterSyncRoot = null;
            _menuParameterSyncAsset = null;
            CloseHubDropdown();
            _hubDropdownAction = null;
            _hubDropdownValueId = null;
            _parameterSerializedObject?.Dispose();
            _parameterSerializedObject = null;
            _parameterSerializedAsset = null;
            SaveProjectAvatarRoot();
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened -= OnAvatarRootSceneOpened;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaved -= OnAvatarRootSceneSaved;
            EditorApplication.playModeStateChanged -= OnAvatarRootPlayModeChanged;
            EditorApplication.delayCall -= RestoreProjectAvatarRoot;
            FinishSetupListResize();
            FinishAnimationToolResize();
            ResetLayerDrag();
            CancelParameterDrag();
            CancelMotionDrag();
            CancelMenuDrag();
            Undo.undoRedoPerformed -= OnHubUndoRedo;
            if (_cachedBehaviourEditor != null) DestroyImmediate(_cachedBehaviourEditor);
            _cachedBehaviourEditor = null;
        }

        private void OnHubUndoRedo()
        {
            _menuParameterSyncDirty = true;
            RestoreProjectAvatarRoot();
            _parameterMergePlan = null;
            _parameterListControllerVersion = _parameterListVrcVersion = -1;
            _parameterUsageScannedName = null;
            _animationToolClipsDirty = true;
            ResetLayerDrag();
            ResetParameterDrag();
            ResetMotionDrag();
            _hasParamCache = false;
            InvalidateLayerSummaries();
            ResetMenuControlSelection();
            _nextMenuSearchRefresh = 0;
            ClearMenuCut();
            ClearMotionCut();
            if (_hasSetupScan) _setupChecksStale = true;
            Repaint();
        }

        private void OnLostFocus()
        {
            CloseHubDropdown();
            _menuNameSelectAllOnClickControl = null;
            FinishSetupListResize();
            FinishAnimationToolResize();
            ResetLayerDrag();
            CancelParameterDrag();
            CancelMotionDrag();
            _isResizingLayerList = false;
            CancelMenuDrag();
            _isResizingMenuList = false;
            Repaint();
        }

        private void OnProjectChange()
        {
            _menuParameterSyncDirty = true;
            RestoreProjectAvatarRoot();
            _parameterMergePlan = null;
            _parameterListControllerVersion = _parameterListVrcVersion = -1;
            _animationToolClipsDirty = true;
            _hasParamCache = false;
            InvalidateLayerSummaries();
            _nextMenuSearchRefresh = 0;
            if (_hasSetupScan) _setupChecksStale = true;
            Repaint();
        }

        private void UpdateMenuParameterReferences()
        {
            if (this == null || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextMenuParameterSyncCheck) return;
            _nextMenuParameterSyncCheck = now + 1d;

            var root = _vrcExpressionMenuAsset;
            var parameters = _vrcExpressionParametersAsset;
            if (root == null || parameters == null)
            {
                // An empty slot should not clear references already saved on menus.
                _menuParameterAssetVersions.Clear();
                _menuParameterSyncRoot = null;
                _menuParameterSyncAsset = null;
                return;
            }

            bool needsSync = _menuParameterSyncDirty || _menuParameterSyncRoot != root || _menuParameterSyncAsset != parameters;
            if (!needsSync)
                foreach (var tracked in _menuParameterAssetVersions)
                    if (tracked.Key == null || EditorUtility.GetDirtyCount(tracked.Key) != tracked.Value || tracked.Key.Parameters != parameters)
                    {
                        needsSync = true;
                        break;
                    }
            if (!needsSync) return;

            _menuParameterSyncDirty = false;
            _menuParameterSyncRoot = root;
            _menuParameterSyncAsset = parameters;
            _menuParameterAssetVersions.Clear();
            var pending = new Stack<VRCExpressionsMenu>();
            var changedMenus = new List<VRCExpressionsMenu>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var menu = pending.Pop();
                // Shared submenus and circular links are visited only once.
                if (menu == null || _menuParameterAssetVersions.ContainsKey(menu)) continue;
                if (menu.Parameters != parameters)
                {
                    // This editor reference follows the loaded slots without adding background Undo steps.
                    menu.Parameters = parameters;
                    EditorUtility.SetDirty(menu);
                    changedMenus.Add(menu);
                }
                _menuParameterAssetVersions.Add(menu, EditorUtility.GetDirtyCount(menu));
                if (menu.controls == null) continue;
                foreach (var control in menu.controls)
                    if (control != null && control.type == Control.ControlType.SubMenu && control.subMenu != null)
                        pending.Push(control.subMenu);
            }

            foreach (var menu in changedMenus)
            {
                if (AssetDatabase.Contains(menu)) AssetDatabase.SaveAssetIfDirty(menu);
                _menuParameterAssetVersions[menu] = EditorUtility.GetDirtyCount(menu);
            }
            if (changedMenus.Count > 0) Repaint();
        }

        private void OnHierarchyChange()
        {
            RestoreProjectAvatarRoot();
            if (_hasSetupScan && _setupAvatarAtScan != null) _setupChecksStale = true;
            Repaint();
        }

        private void OnGUI()
        {
            _hubGuiScreenOrigin = GUIUtility.GUIToScreenPoint(Vector2.zero);
            HandleHubDropdownInput();
            Vector2 mousePosition = Event.current.mousePosition;
            bool coverHover = _hubDropdown != null && Event.current.type == EventType.Repaint;
            if (coverHover) Event.current.mousePosition = new Vector2(-100000f, -100000f);
            try
            {
                // Handle Escape before text fields or other controls can consume it.
                HandleOtherDragCancellation();
                if (_isParamDragging && (_paramDragController == null || _paramDragController != GetAnimatorController() || !_showParamManager))
                    CancelParameterDrag();
                if (_isDragging && (_motionDragTree == null || _motionDragTree != CurrentTree || !_showBlendTreeEditor))
                    CancelMotionDrag();
                if (_isMenuDragging && (_menuDragOwner == null || !_showMenuEditor || GUIUtility.hotControl != _menuDragControlId))
                    CancelMenuDrag();
                DrawHeader();

                _mainScrollPos = EditorGUILayout.BeginScrollView(_mainScrollPos);
                DrawSetupChecks();

                AnimatorController controller = GetAnimatorController();
                if (controller != null)
                    DrawParameterManager(controller);
                DrawMenuEditorSection();

                if (controller != null)
                {
                    DrawLayerEditorSection(controller);
                    DrawBlendTreeEditorSection();
                }
                else if (_targetAsset == null && (_showParamManager || _showLayerEditor || _showBlendTreeEditor))
                    EditorGUILayout.HelpBox("Assign an Animation Controller above to edit parameters, layers and Blend Trees.", MessageType.Info);

                DrawToggleMakerSection();
                DrawAnimationToolSection(0, "Blendshape Mapper", _showBlendshapeMapper);
                DrawAnimationToolSection(1, "Animation Path Replacer", _showPathReplaceEditor);
                DrawAnimationToolSection(3, "Merger", _showAnimatorLayerMerger);

                EditorGUILayout.EndScrollView();
            }
            finally { if (coverHover) Event.current.mousePosition = mousePosition; }
            DrawHubDropdown();
            var dropdownAction = _hubDropdownAction;
            _hubDropdownAction = null;
            dropdownAction?.Invoke();
            if (Event.current.type == EventType.Layout) _hubDropdownValueId = null;
        }

        [NonSerialized] private HubDropdownMenu _hubDropdown;
        [NonSerialized] private Action _hubDropdownAction;
        [NonSerialized] private string _hubDropdownValueId;
        [NonSerialized] private int _hubDropdownValue;
        [NonSerialized] private string _hubDropdownButtonOwnerId, _hubDropdownOwnerId;
        [NonSerialized] private GUIStyle _hubDropdownPopupStyle;
        [NonSerialized] private bool _hubDropdownPopupProSkin;
        private Vector2 _hubGuiScreenOrigin;
        private Rect _hubDropdownButtonAnchor, _hubDropdownAnchor;
        private readonly List<HubDropdownItem> _hubDropdownPath = new List<HubDropdownItem>();
        private float _hubDropdownScroll;
        private Vector2 _hubDropdownPageSize;
        private int _hubDropdownHover = -1, _hubDropdownPressed = -1;
        private bool _hubDropdownPressedButton;
        private bool _hubDropdownDraggingScroll;
        private bool _hubDropdownPreviousMouseMove;
        private float _hubDropdownThumbOffset;
        private const float HubDropdownRowHeight = 22f;
        private const float HubDropdownActionWidth = 60f;

        private sealed class HubDropdownItem
        {
            public GUIContent content;
            public bool selected, disabled, separator, stayOpen;
            public Action action;
            public Func<bool> selectedNow, enabledNow;
            public GUIContent buttonContent;
            public Action buttonAction;
            public Func<bool> buttonEnabledNow;
            public readonly List<HubDropdownItem> children = new List<HubDropdownItem>();
            public bool Enabled => !disabled && (enabledNow == null || enabledNow());
            public bool Selected => selectedNow != null ? selectedNow() : selected;
            public bool ButtonEnabled => Enabled && buttonAction != null && (buttonEnabledNow == null || buttonEnabledNow());
        }

        // All of the hub's menus share an overlay in this EditorWindow. No native popup
        // can be placed outside the tool, and choosing a colour section can keep it open.
        private sealed class HubDropdownMenu
        {
            private readonly BlendTreeWindow _owner;
            private readonly bool _splitPaths;
            public readonly List<HubDropdownItem> items = new List<HubDropdownItem>();

            public HubDropdownMenu(BlendTreeWindow owner, bool splitPaths = false)
            { _owner = owner; _splitPaths = splitPaths; }

            public void AddItem(GUIContent content, bool selected, Action action, bool stayOpen = false,
                Func<bool> selectedNow = null, Func<bool> enabledNow = null)
            {
                Add(content, new HubDropdownItem { selected = selected, action = action, stayOpen = stayOpen,
                    selectedNow = selectedNow, enabledNow = enabledNow });
            }

            public void AddDisabledItem(GUIContent content)
            { Add(content, new HubDropdownItem { disabled = true }); }

            private void Add(GUIContent content, HubDropdownItem item)
            {
                var destination = items;
                string[] path = _splitPaths ? content.text.Split('/') : new[] { content.text };
                for (int i = 0; i < path.Length - 1; i++)
                {
                    var group = destination.Find(entry => !entry.separator && entry.action == null && !entry.disabled && entry.content.text == path[i]);
                    if (group == null) { group = new HubDropdownItem { content = new GUIContent(path[i]) }; destination.Add(group); }
                    destination = group.children;
                }
                item.content = new GUIContent(content) { text = path[path.Length - 1] };
                destination.Add(item);
            }

            public void AddSeparator(string unused) { items.Add(new HubDropdownItem { separator = true }); }
            public void ShowAsContext() { _owner.OpenHubDropdown(this); }
        }

        private bool HubDropdownButton(string text, params GUILayoutOption[] options)
        { return HubDropdownButton(new GUIContent(text), GUI.skin.button, options); }
        private bool HubDropdownButton(GUIContent content, params GUILayoutOption[] options)
        { return HubDropdownButton(content, GUI.skin.button, options); }
        private bool HubDropdownButton(string text, GUIStyle style, params GUILayoutOption[] options)
        { return HubDropdownButton(new GUIContent(text), style, options); }
        private bool HubDropdownButton(GUIContent content, GUIStyle style, params GUILayoutOption[] options)
        {
            // Repeated labels (Options, Show, or arrow-only buttons) need separate owners.
            int controlId = GUIUtility.GetControlID("HubDropdownButton".GetHashCode(), FocusType.Passive);
            return HubDropdownButtonWithId("HubButton_" + controlId, content, style, options);
        }

        private bool HubDropdownButtonWithId(string id, GUIContent content, GUIStyle style, params GUILayoutOption[] options)
        {
            Rect rect = GUILayoutUtility.GetRect(content, style, options);
            return DrawHubDropdownButton(rect, content, style, id);
        }

        private bool DrawHubDropdownButton(Rect rect, GUIContent content, GUIStyle style, string id)
        {
            bool opened = _hubDropdown != null && _hubDropdownOwnerId == id;
            bool popupArrow = style == EditorStyles.popup;
            if (popupArrow)
            {
                // The native popup background has a fixed down arrow, so draw our own.
                if (_hubDropdownPopupStyle == null || _hubDropdownPopupProSkin != EditorGUIUtility.isProSkin)
                {
                    _hubDropdownPopupProSkin = EditorGUIUtility.isProSkin;
                    _hubDropdownPopupStyle = new GUIStyle(EditorStyles.miniButton)
                    {
                        alignment = TextAnchor.MiddleLeft,
                        fontSize = EditorStyles.popup.fontSize,
                        padding = new RectOffset(6, 20, 0, 0)
                    };
                }
                style = _hubDropdownPopupStyle;
            }
            else
            {
                string text = (content.text ?? "").TrimEnd();
                string label = text.TrimEnd('\u25be', '\u25bc', '\u25b8', '\u25b6', '\u25ba', '\u25b2', '\u25b4').TrimEnd();
                if (label != text)
                {
                    content = new GUIContent(content)
                    {
                        text = (label.Length > 0 ? label + " " : "") + (opened ? "\u25be" : "\u25b8")
                    };
                }
            }
            bool clicked = GUI.Button(rect, content, style);
            if (popupArrow)
                GUI.Label(new Rect(rect.xMax - 16f, rect.y, 14f, rect.height), opened ? "\u25be" : "\u25b8", EditorStyles.miniLabel);
            if (!clicked) return false;
            // Capture the actual button rect before the click makes GetLastRect unreliable.
            _hubDropdownButtonAnchor = new Rect(GUIUtility.GUIToScreenPoint(rect.position) - _hubGuiScreenOrigin, rect.size);
            _hubDropdownButtonOwnerId = id;
            return true;
        }

        private void OpenHubDropdown(HubDropdownMenu menu)
        {
            if (_hubDropdown == null) _hubDropdownPreviousMouseMove = wantsMouseMove;
            wantsMouseMove = true;
            _hubDropdown = menu;
            _hubDropdownOwnerId = _hubDropdownButtonOwnerId;
            _hubDropdownAnchor = _hubDropdownButtonAnchor;
            _hubDropdownPath.Clear();
            _hubDropdownScroll = 0f;
            _hubDropdownHover = _hubDropdownPressed = -1;
            _hubDropdownPressedButton = false;
            _hubDropdownDraggingScroll = false;
            MeasureHubDropdownPage();
            GUI.FocusControl(null);
            GUIUtility.hotControl = 0;
            Repaint();
        }

        private void CloseHubDropdown()
        {
            if (_hubDropdown != null) wantsMouseMove = _hubDropdownPreviousMouseMove;
            _hubDropdown = null;
            _hubDropdownOwnerId = _hubDropdownButtonOwnerId = null;
            _hubDropdownPath.Clear();
            _hubDropdownDraggingScroll = false;
            _hubDropdownPressed = -1;
            _hubDropdownPressedButton = false;
            Repaint();
        }

        private List<HubDropdownItem> HubDropdownItems => _hubDropdownPath.Count == 0 ? _hubDropdown.items :
            _hubDropdownPath[_hubDropdownPath.Count - 1].children;

        private void MeasureHubDropdownPage()
        {
            float width = Mathf.Max(220f, _hubDropdownAnchor.width);
            float contentHeight = 0f;
            foreach (var item in HubDropdownItems)
            {
                contentHeight += item.separator ? 6f : HubDropdownRowHeight;
                if (!item.separator) width = Mathf.Max(width, EditorStyles.label.CalcSize(item.content).x + 48f +
                    (item.buttonAction != null ? HubDropdownActionWidth + 4f : 0f));
            }
            _hubDropdownPageSize = new Vector2(width, contentHeight);
        }

        private Rect GetHubDropdownRect(out Rect viewport, out float contentHeight)
        {
            contentHeight = _hubDropdownPageSize.y;
            float header = _hubDropdownPath.Count > 0 ? HubDropdownRowHeight + 6f : 0f;
            float width = Mathf.Min(_hubDropdownPageSize.x, 440f, Mathf.Max(1f, position.width - 8f));
            float height = Mathf.Min(contentHeight + header + 8f, 460f, Mathf.Max(1f, position.height - 8f));
            float x = Mathf.Clamp(_hubDropdownAnchor.x, 4f, Mathf.Max(4f, position.width - width - 4f));
            float y = _hubDropdownAnchor.yMax + 2f;
            if (y + height > position.height - 4f) y = _hubDropdownAnchor.y - height - 2f;
            y = Mathf.Clamp(y, 4f, Mathf.Max(4f, position.height - height - 4f));
            var rect = new Rect(x, y, width, height);
            viewport = new Rect(x + 4f, y + 4f + header, width - 8f, Mathf.Max(1f, height - header - 8f));
            _hubDropdownScroll = Mathf.Clamp(_hubDropdownScroll, 0f, Mathf.Max(0f, contentHeight - viewport.height));
            return rect;
        }

        private Rect GetHubDropdownThumb(Rect viewport, float contentHeight)
        {
            float height = Mathf.Min(viewport.height, Mathf.Max(18f, viewport.height * viewport.height / contentHeight));
            return new Rect(viewport.xMax - 10f, viewport.y + (viewport.height - height) *
                _hubDropdownScroll / Mathf.Max(1f, contentHeight - viewport.height), 8f, height);
        }

        private static Rect GetHubDropdownActionRect(Rect row)
        {
            return new Rect(Mathf.Max(row.x, row.xMax - HubDropdownActionWidth - 2f), row.y + 1f,
                Mathf.Min(HubDropdownActionWidth, row.width), row.height - 2f);
        }

        private void ActivateHubDropdownItem(int index)
        {
            var items = HubDropdownItems;
            if (index < 0 || index >= items.Count || items[index].separator || !items[index].Enabled) return;
            var item = items[index];
            if (item.children.Count > 0)
            {
                _hubDropdownPath.Add(item);
                MeasureHubDropdownPage();
                _hubDropdownScroll = 0f;
                _hubDropdownHover = _hubDropdownPressed = -1;
            }
            else
            {
                _hubDropdownAction = item.action;
                if (!item.stayOpen) CloseHubDropdown();
            }
            Repaint();
        }

        private void BackHubDropdown()
        {
            if (_hubDropdownPath.Count == 0) return;
            _hubDropdownPath.RemoveAt(_hubDropdownPath.Count - 1);
            MeasureHubDropdownPage();
            _hubDropdownScroll = 0f;
            _hubDropdownHover = _hubDropdownPressed = -1;
            Repaint();
        }

        private void HandleHubDropdownInput()
        {
            if (_hubDropdown == null) return;
            Event e = Event.current;
            if (e.type == EventType.Layout || e.type == EventType.Repaint || e.type == EventType.Used) return;
            // Opening a dropdown releases text focus. Let Unity's delayed fields
            // receive their commit command before the overlay handles more input.
            if (e.type == EventType.ExecuteCommand && e.commandName == "DelayedControlShouldCommit") return;
            Rect rect = GetHubDropdownRect(out Rect viewport, out float contentHeight);
            var items = HubDropdownItems;
            bool scrollable = contentHeight > viewport.height;
            Rect track = new Rect(viewport.xMax - 12f, viewport.y, 12f, viewport.height);
            int hovered = -1;
            bool hoveredButton = false;
            if (viewport.Contains(e.mousePosition) && !(scrollable && track.Contains(e.mousePosition)))
            {
                float y = viewport.y - _hubDropdownScroll;
                for (int i = 0; i < items.Count; i++)
                {
                    float height = items[i].separator ? 6f : HubDropdownRowHeight;
                    if (!items[i].separator && e.mousePosition.y >= y && e.mousePosition.y < y + height)
                    {
                        hovered = i;
                        var row = new Rect(viewport.x, y, viewport.width - (scrollable ? 14f : 0f), height);
                        hoveredButton = items[i].buttonAction != null && GetHubDropdownActionRect(row).Contains(e.mousePosition);
                        break;
                    }
                    y += height;
                }
            }
            if (e.type == EventType.MouseMove)
            { _hubDropdownHover = hovered; Repaint(); }
            else if (e.type == EventType.ScrollWheel)
            { _hubDropdownScroll = Mathf.Clamp(_hubDropdownScroll + e.delta.y * 18f, 0f, Mathf.Max(0f, contentHeight - viewport.height)); Repaint(); }
            else if (e.type == EventType.MouseDown)
            {
                if (!rect.Contains(e.mousePosition)) CloseHubDropdown();
                else if (e.button == 0 && scrollable && track.Contains(e.mousePosition))
                {
                    Rect thumb = GetHubDropdownThumb(viewport, contentHeight);
                    _hubDropdownDraggingScroll = true;
                    _hubDropdownThumbOffset = thumb.Contains(e.mousePosition) ? e.mousePosition.y - thumb.y : thumb.height * 0.5f;
                }
                else if (e.button == 0)
                {
                    _hubDropdownPressed = hovered;
                    _hubDropdownPressedButton = hoveredButton;
                    if (_hubDropdownPath.Count > 0 && e.mousePosition.y < viewport.y) _hubDropdownPressed = -2;
                    Repaint();
                }
            }
            if (_hubDropdownDraggingScroll && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag))
            {
                Rect thumb = GetHubDropdownThumb(viewport, contentHeight);
                _hubDropdownScroll = Mathf.Clamp01((e.mousePosition.y - viewport.y - _hubDropdownThumbOffset) /
                    Mathf.Max(1f, viewport.height - thumb.height)) * (contentHeight - viewport.height);
                Repaint();
            }
            if (e.type == EventType.MouseUp && e.button == 0 && _hubDropdown != null)
            {
                if (!_hubDropdownDraggingScroll)
                {
                    if (_hubDropdownPressed == -2 && rect.Contains(e.mousePosition) && e.mousePosition.y < viewport.y) BackHubDropdown();
                    else if (_hubDropdownPressed == hovered && _hubDropdownPressedButton == hoveredButton)
                    {
                        if (hoveredButton && hovered >= 0)
                        {
                            if (items[hovered].ButtonEnabled)
                            {
                                _hubDropdownAction = items[hovered].buttonAction;
                                CloseHubDropdown();
                            }
                        }
                        else ActivateHubDropdownItem(hovered);
                    }
                }
                _hubDropdownPressed = -1;
                _hubDropdownPressedButton = false;
                _hubDropdownDraggingScroll = false;
                Repaint();
            }
            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape) CloseHubDropdown();
                else if (e.keyCode == KeyCode.LeftArrow) BackHubDropdown();
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.keyCode == KeyCode.RightArrow)
                    ActivateHubDropdownItem(_hubDropdownHover);
                else if (e.keyCode == KeyCode.UpArrow || e.keyCode == KeyCode.DownArrow)
                {
                    int step = e.keyCode == KeyCode.UpArrow ? -1 : 1;
                    for (int i = 0, next = _hubDropdownHover; i < items.Count; i++)
                    {
                        next = (next + step + items.Count) % items.Count;
                        if (items[next].separator || !items[next].Enabled) continue;
                        _hubDropdownHover = next;
                        float top = 0f;
                        for (int j = 0; j < next; j++) top += items[j].separator ? 6f : HubDropdownRowHeight;
                        if (top < _hubDropdownScroll) _hubDropdownScroll = top;
                        if (top + HubDropdownRowHeight > _hubDropdownScroll + viewport.height) _hubDropdownScroll = top + HubDropdownRowHeight - viewport.height;
                        break;
                    }
                    Repaint();
                }
            }
            // Consume input before drawing the underlying controls, including outside clicks.
            e.Use();
        }

        private void DrawHubDropdown()
        {
            if (_hubDropdown == null || Event.current.type != EventType.Repaint) return;
            Rect rect = GetHubDropdownRect(out Rect viewport, out float contentHeight);
            bool dark = EditorGUIUtility.isProSkin;
            Color oldColour = GUI.color;
            GUI.color = Color.white;
            var style = new GUIStyle(EditorStyles.label) { clipping = TextClipping.Clip, richText = false,
                alignment = TextAnchor.MiddleLeft, padding = new RectOffset(0, 0, 0, 0) };
            EditorGUI.DrawRect(rect, dark ? new Color(0.12f, 0.12f, 0.12f) : new Color(0.48f, 0.48f, 0.48f));
            EditorGUI.DrawRect(new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f),
                dark ? new Color(0.22f, 0.22f, 0.22f) : new Color(0.95f, 0.95f, 0.95f));
            if (_hubDropdownPath.Count > 0)
                style.Draw(new Rect(rect.x + 8f, rect.y + 4f, rect.width - 16f, HubDropdownRowHeight),
                    new GUIContent("‹  " + _hubDropdownPath[_hubDropdownPath.Count - 1].content.text), false, false, false, false);
            GUI.BeginClip(viewport);
            try
            {
                float width = viewport.width - (contentHeight > viewport.height ? 14f : 0f);
                float y = -_hubDropdownScroll;
                var items = HubDropdownItems;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    float height = item.separator ? 6f : HubDropdownRowHeight;
                    if (y + height >= 0f && y < viewport.height)
                    {
                        if (item.separator) EditorGUI.DrawRect(new Rect(4f, y + 2f, width - 8f, 1f), new Color(0.5f, 0.5f, 0.5f, 0.35f));
                        else
                        {
                            bool enabled = item.Enabled;
                            if (enabled && i == _hubDropdownHover) EditorGUI.DrawRect(new Rect(0f, y, width, height), new Color(0.24f, 0.49f, 0.75f, 0.35f));
                            style.normal.textColor = enabled ? (dark ? new Color(0.86f, 0.86f, 0.86f) : Color.black) : Color.gray;
                            if (item.Selected) style.Draw(new Rect(4f, y, 18f, height), new GUIContent("✓"), false, false, false, false);
                            float actionWidth = item.buttonAction != null ? HubDropdownActionWidth + 4f : 0f;
                            style.Draw(new Rect(24f, y, Mathf.Max(1f, width - 44f - actionWidth), height), item.content, false, false, false, false);
                            if (item.children.Count > 0) style.Draw(new Rect(width - 16f, y, 14f, height), new GUIContent("›"), false, false, false, false);
                            if (item.buttonAction != null)
                            {
                                Rect button = GetHubDropdownActionRect(new Rect(0f, y, width, height));
                                using (new EditorGUI.DisabledScope(!item.ButtonEnabled))
                                    EditorStyles.miniButton.Draw(button, item.buttonContent, button.Contains(Event.current.mousePosition),
                                        _hubDropdownPressed == i && _hubDropdownPressedButton, false, false);
                            }
                        }
                    }
                    y += height;
                }
            }
            finally { GUI.EndClip(); GUI.color = oldColour; }
            if (contentHeight > viewport.height)
            {
                EditorGUI.DrawRect(new Rect(viewport.xMax - 12f, viewport.y, 12f, viewport.height), new Color(0.1f, 0.1f, 0.1f, 0.2f));
                EditorGUI.DrawRect(GetHubDropdownThumb(viewport, contentHeight), new Color(0.5f, 0.5f, 0.5f, 0.8f));
            }
        }

        private void QueueHubDropdownValue(string id, int value)
        { _hubDropdownValueId = id; _hubDropdownValue = value; Repaint(); }

        private int ConsumeHubDropdownValue(string id, int current)
        {
            if (_hubDropdownValueId == id && Event.current.type == EventType.Layout && GUI.enabled)
            {
                _hubDropdownValueId = null;
                if (_hubDropdownValue != current) { GUI.changed = true; return _hubDropdownValue; }
            }
            return current;
        }

        private int HubIntPopup(string id, int current, string[] labels, int[] values, params GUILayoutOption[] options)
        { return HubIntPopup(id, null, current, labels, values, options); }

        private int HubIntPopup(string id, string label, int current, string[] labels, int[] values, params GUILayoutOption[] options)
        {
            current = ConsumeHubDropdownValue(id, current);
            int index = Array.IndexOf(values, current);
            Rect rect = EditorGUILayout.GetControlRect(!string.IsNullOrEmpty(label), EditorGUIUtility.singleLineHeight, options);
            if (!string.IsNullOrEmpty(label)) rect = EditorGUI.PrefixLabel(rect, new GUIContent(label));
            if (DrawHubDropdownButton(rect, new GUIContent(index >= 0 && index < labels.Length ? labels[index] : "—"), EditorStyles.popup, id))
            {
                var menu = new HubDropdownMenu(this);
                for (int i = 0; i < labels.Length && i < values.Length; i++)
                {
                    int value = values[i];
                    menu.AddItem(new GUIContent(labels[i]), value == current, () => QueueHubDropdownValue(id, value));
                }
                menu.ShowAsContext();
            }
            return current;
        }

        private int HubPopup(string id, string label, int current, string[] labels, params GUILayoutOption[] options)
        {
            var values = new int[labels.Length];
            for (int i = 0; i < values.Length; i++) values[i] = i;
            return HubIntPopup(id, label, current, labels, values, options);
        }

        private Enum HubEnumPopup(string id, string label, Enum current, params GUILayoutOption[] options)
        {
            Type type = current.GetType();
            var names = Enum.GetNames(type);
            var rawValues = Enum.GetValues(type);
            var values = new int[names.Length];
            for (int i = 0; i < names.Length; i++) { names[i] = ObjectNames.NicifyVariableName(names[i]); values[i] = Convert.ToInt32(rawValues.GetValue(i)); }
            return (Enum)Enum.ToObject(type, HubIntPopup(id, label, Convert.ToInt32(current), names, values, options));
        }

        private int HubMaskField(string id, int current, string[] labels, params GUILayoutOption[] options)
        {
            current = ConsumeHubDropdownValue(id, current);
            int all = (1 << labels.Length) - 1;
            string display = (current & all) == all ? "Everything" : current == 0 ? "Nothing" : "Mixed…";
            for (int i = 0; i < labels.Length; i++) if (current == (1 << i)) display = labels[i];
            if (HubDropdownButtonWithId(id, new GUIContent(display), EditorStyles.popup, options))
            {
                var menu = new HubDropdownMenu(this);
                int mask = current;
                menu.AddItem(new GUIContent("Nothing"), false, () => { mask = 0; QueueHubDropdownValue(id, mask); }, true, () => mask == 0);
                menu.AddItem(new GUIContent("Everything"), false, () => { mask = -1; QueueHubDropdownValue(id, mask); }, true, () => (mask & all) == all);
                menu.AddSeparator("");
                for (int i = 0; i < labels.Length; i++)
                {
                    int bit = 1 << i;
                    menu.AddItem(new GUIContent(labels[i]), false, () => { mask ^= bit; QueueHubDropdownValue(id, mask); }, true, () => (mask & bit) != 0);
                }
                menu.ShowAsContext();
            }
            return current;
        }

        private sealed class NestedScrollViewScope : EditorGUILayout.ScrollViewScope
        {
            public NestedScrollViewScope(ref Vector2 position, params GUILayoutOption[] options)
                : base(position, options)
            {
                position = scrollPosition;
            }

            protected override void CloseScope()
            {
                Event e = Event.current;
                bool verticalWheel = e.type == EventType.ScrollWheel && e.delta.y != 0f &&
                    !e.shift && Mathf.Abs(e.delta.y) >= Mathf.Abs(e.delta.x);
                if (verticalWheel)
                {
                    // With a zero movement allowance, ScrollTowards only checks whether this view
                    // can scroll further. It uses Unity's actual content and viewport bounds.
                    float targetY = e.delta.y > 0f ? float.MaxValue : 0f;
                    handleScrollWheel = GUI.ScrollTowards(new Rect(scrollPosition.x, targetY, 0f, 0f), 0f);
                }
                // At either limit, leave the wheel event for the enclosing scroll view.
                base.CloseScope();
            }
        }

        private string _pendingComboChangeValue = null;
        private string _pendingComboChangeId = null;

        private List<string> GetAvailableParameterNames()
        {
            var options = new List<string>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var controller = GetAnimatorController();
            var animatorParameters = controller != null ? controller.parameters : Array.Empty<AnimatorControllerParameter>();
            var vrcNames = GetVrcParameterNames(_vrcExpressionParametersAsset);
            foreach (var row in BuildParameterOrder(animatorParameters, vrcNames))
            {
                string name = row.animatorIndex >= 0 ? animatorParameters[row.animatorIndex].name : vrcNames[row.vrcIndex];
                if (!string.IsNullOrEmpty(name) && names.Add(name)) options.Add(name);
            }
            return options;
        }
        private string DrawFlexibleParameterComboBox(string controlId, string currentValue)
        {
            if (_pendingComboChangeId == controlId && _pendingComboChangeValue != null)
            {
                currentValue = _pendingComboChangeValue;
                _pendingComboChangeId = null;
                _pendingComboChangeValue = null;
                GUI.FocusControl(null);
                GUI.changed = true;
            }

            EditorGUILayout.BeginHorizontal();
            GUI.SetNextControlName(controlId);
            string newValue = EditorGUILayout.TextField(currentValue);

            if (HubDropdownButtonWithId(controlId, new GUIContent("▼"), EditorStyles.miniButton, GUILayout.Width(20)))
            {
                HubDropdownMenu menu = new HubDropdownMenu(this);
                List<string> options = GetAvailableParameterNames();
                if (options.Count == 0) options.Add("None");

                foreach (string opt in options)
                {
                    menu.AddItem(new GUIContent(opt), opt == currentValue, () =>
                    {
                        _pendingComboChangeId = controlId;
                        _pendingComboChangeValue = opt;
                    });
                }
                menu.ShowAsContext();
            }
            EditorGUILayout.EndHorizontal();

            return newValue;
        }

        private string DrawParameterComboBoxWithLabel(string controlId, string label, string currentValue)
        {
            if (_pendingComboChangeId == controlId && _pendingComboChangeValue != null)
            {
                currentValue = _pendingComboChangeValue;
                _pendingComboChangeId = null;
                _pendingComboChangeValue = null;
                GUI.FocusControl(null);
                GUI.changed = true;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(EditorGUIUtility.labelWidth - 4));

            GUI.SetNextControlName(controlId);
            string newValue = EditorGUILayout.TextField(currentValue);

            if (HubDropdownButtonWithId(controlId, new GUIContent("▼"), EditorStyles.miniButton, GUILayout.Width(20)))
            {
                HubDropdownMenu menu = new HubDropdownMenu(this);
                List<string> options = GetAvailableParameterNames();
                if (options.Count == 0) options.Add("None");

                foreach (string opt in options)
                {
                    menu.AddItem(new GUIContent(opt), opt == currentValue, () =>
                    {
                        _pendingComboChangeId = controlId;
                        _pendingComboChangeValue = opt;
                    });
                }
                menu.ShowAsContext();
            }
            EditorGUILayout.EndHorizontal();

            return newValue;
        }

        private void DrawBlendTreeListResizer()
        {
            Event e = Event.current;
            Rect resizerRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(6), GUILayout.ExpandWidth(true));
            EditorGUIUtility.AddCursorRect(resizerRect, MouseCursor.ResizeVertical);

            if (e.type == EventType.Repaint)
            {
                Rect lineRect = new Rect(resizerRect.x, resizerRect.y + 2, resizerRect.width, 2);
                EditorGUI.DrawRect(lineRect, new Color(0.15f, 0.15f, 0.15f, 1f));
            }

            if (e.type == EventType.MouseDown && e.button == 0 && resizerRect.Contains(e.mousePosition))
            {
                _isResizingBlendTreeList = true;
                e.Use();
            }
            if (_isResizingBlendTreeList && e.type == EventType.MouseDrag)
            {
                _blendTreeListHeight += e.delta.y;
                _blendTreeListHeight = Mathf.Clamp(_blendTreeListHeight, 60f, 1000f);
                Repaint();
                e.Use();
            }
            if (_isResizingBlendTreeList && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp))
            {
                _isResizingBlendTreeList = false;
                EditorPrefs.SetFloat(PrefBlendTreeListHeightKey, _blendTreeListHeight);
                e.Use();
            }
        }

        private void DrawBlendTreeEditorSection()
        {
            if (!_showBlendTreeEditor) CancelMotionDrag();
            if (_showBlendTreeEditor)
            {
                GUILayout.Space(5);
                EditorGUILayout.LabelField("BlendTree Editor", EditorStyles.boldLabel);
                EditorGUILayout.BeginVertical("box");

                if (_navigationPath.Count == 0)
                {
                    DrawRootSelection();
                }
                else
                {
                    BlendTree current = CurrentTree;
                    if (current == null)
                    {
                        _navigationPath.Clear();
                        EditorGUILayout.EndVertical();
                        return;
                    }

                    DrawBreadcrumbs();
                    DrawBlendTreeSettings();
                    DrawAnimationConverter();
                    DrawChildMotions();
                }

                EditorGUILayout.EndVertical();
            }
        }

        private void DrawToggleMakerSection()
        {
            if (!_showToggleMaker) return;
            GUILayout.Space(5);
            EditorGUILayout.LabelField("Animations Recorder", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            using (new EditorGUI.DisabledScope(_toggleRecording))
                _AnimationRecorderClip = (AnimationClip)EditorGUILayout.ObjectField("Animation", _AnimationRecorderClip, typeof(AnimationClip), false);
            bool busy = EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode;
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_toggleRecording || busy))
            {
                if (GUILayout.Button("New Animation", GUILayout.Width(115)))
                    EditorApplication.delayCall += () => { if (this != null) CreateToggleMakerAnimation(); };
            }
            GUILayout.FlexibleSpace();
            if (_toggleRecording)
            {
                if (GUILayout.Button("Select Recording Copy", GUILayout.Width(165))) Selection.activeGameObject = _toggleRecordingCopy;
                if (GUILayout.Button("Stop Recording", GUILayout.Width(120)))
                    EditorApplication.delayCall += () => { if (this != null) StopToggleMakerRecording(); };
            }
            else
            {
                using (new EditorGUI.DisabledScope(busy || _avatarRoot == null))
                    if (GUILayout.Button(new GUIContent("Record", "Record on a temporary avatar copy. Other avatars are hidden and the Hub closes until recording stops. Stop with the red Record button in Unity's Animation window."), GUILayout.Width(90)))
                        EditorApplication.delayCall += () => { if (this != null) StartToggleMakerRecording(); };
            }
            EditorGUILayout.EndHorizontal();
            if (_toggleRecording)
                EditorGUILayout.LabelField("Recording: edit the recording copy in the Hierarchy, Inspector or Scene view. Use the Animation window for keyframes. Stopping saves the animation and restores the original avatar.", EditorStyles.wordWrappedMiniLabel);
            else if (_avatarRoot == null)
                EditorGUILayout.LabelField("Assign Avatar Root above, then create or drag in an animation to record.", EditorStyles.wordWrappedMiniLabel);
            else if (_AnimationRecorderClip != null && !IsToggleMakerClipEditable(_AnimationRecorderClip))
                EditorGUILayout.LabelField("Assign an editable .anim file from Assets, or use New Animation.", EditorStyles.wordWrappedMiniLabel);
            else
                EditorGUILayout.HelpBox("Create or drag in an animation, then press Record.", MessageType.Info);
            EditorGUILayout.EndVertical();
        }

        private static bool IsToggleMakerClipEditable(AnimationClip clip)
        {
            if (clip == null || clip.legacy || !AssetDatabase.IsMainAsset(clip)) return false;
            string path = AssetDatabase.GetAssetPath(clip);
            return path.StartsWith("Assets/", StringComparison.Ordinal) && path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) &&
                AssetDatabase.IsOpenForEdit(clip);
        }

        private bool CreateToggleMakerAnimation()
        {
            if (_toggleRecording || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode) return false;
            string folder = "Assets";
            string selectedPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!string.IsNullOrEmpty(selectedPath) && (selectedPath == "Assets" || selectedPath.StartsWith("Assets/", StringComparison.Ordinal)))
                folder = AssetDatabase.IsValidFolder(selectedPath) ? selectedPath : System.IO.Path.GetDirectoryName(selectedPath).Replace('\\', '/');
            string path = EditorUtility.SaveFilePanelInProject("New Toggle Animation",
                "New Toggle", "anim", "Choose where to save the animation.", folder);
            if (string.IsNullOrEmpty(path)) return false;
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            AnimationClip clip = new AnimationClip { frameRate = 60f };
            clip.name = System.IO.Path.GetFileNameWithoutExtension(path);
            clip.hideFlags = HideFlags.None;
            clip.legacy = false;
            try
            {
                AssetDatabase.CreateAsset(clip, path);
                AssetDatabase.SaveAssetIfDirty(clip);
                _AnimationRecorderClip = clip;
                EditorGUIUtility.PingObject(clip);
                Repaint();
                return true;
            }
            catch (Exception exception)
            {
                if (!AssetDatabase.Contains(clip)) DestroyImmediate(clip);
                EditorUtility.DisplayDialog("Animations Recorder", "Could not create the animation.\n\n" + exception.Message, "OK");
                return false;
            }
        }

        private static object GetToggleRecorderState(AnimationWindow window)
        {
            var property = typeof(AnimationWindow).GetProperty("state", ToggleRecorderFlags);
            if (property == null) throw new InvalidOperationException("This Unity version does not expose the animation recording controls.");
            var state = property.GetValue(window);
            if (state == null) throw new InvalidOperationException("The Animation window is not ready. Try Record again.");
            return state;
        }

        private static GameObject GetToggleRecorderRoot(object state)
        {
            return state?.GetType().GetProperty("activeRootGameObject", ToggleRecorderFlags)?.GetValue(state) as GameObject;
        }

        private sealed class ToggleMakerPose
        {
            private readonly string _name;
            private readonly Vector3 _localPosition;
            private readonly Quaternion _localRotation;
            private readonly Vector3 _localScale;
            private readonly ToggleMakerPose[] _children;

            public ToggleMakerPose(Transform transform)
            {
                _name = transform.name;
                _localPosition = transform.localPosition;
                _localRotation = transform.localRotation;
                _localScale = transform.localScale;
                _children = new ToggleMakerPose[transform.childCount];
                for (int i = 0; i < _children.Length; i++)
                    _children[i] = new ToggleMakerPose(transform.GetChild(i));
            }

            public void Apply(Transform transform)
            {
                // Restore scene values before recording starts; never turn the starting pose into curves.
                transform.localPosition = _localPosition;
                transform.localRotation = _localRotation;
                transform.localScale = _localScale;
                for (int i = 0; i < _children.Length && i < transform.childCount; i++)
                {
                    var child = transform.GetChild(i);
                    if (child.name == _children[i]._name) _children[i].Apply(child);
                }
            }
        }

        private sealed class RecordingHubWindowState
        {
            public string SerializedState;
            public Rect Position;
            public bool Maximized;
            public UnityEngine.Object Dock;
            public int TabIndex;
            public UnityEngine.Object Controller;
            public VRCExpressionParameters Parameters;
            public VRCExpressionsMenu Menu;
            public List<BlendTree> TreePath;
            public List<VRCExpressionsMenu> MenuPath;
            public Vector2 Scroll;

            public RecordingHubWindowState(BlendTreeWindow hub, UnityEngine.Object host)
            {
                SerializedState = EditorJsonUtility.ToJson(hub);
                Position = hub.position;
                Maximized = hub.maximized;
                var panes = host?.GetType().GetField("m_Panes", ToggleRecorderFlags)?.GetValue(host) as List<EditorWindow>;
                if (panes != null) { Dock = host; TabIndex = panes.IndexOf(hub); }
                Controller = hub._targetAsset;
                Parameters = hub._vrcExpressionParametersAsset;
                Menu = hub._vrcExpressionMenuAsset;
                TreePath = new List<BlendTree>(hub._navigationPath);
                MenuPath = new List<VRCExpressionsMenu>(hub._menuNavigationPath);
                Scroll = hub._mainScrollPos;
            }

            public void Reopen()
            {
                var hub = CreateInstance<BlendTreeWindow>();
                EditorJsonUtility.FromJsonOverwrite(SerializedState, hub);
                hub._targetAsset = Controller;
                hub._vrcExpressionParametersAsset = Parameters;
                hub._vrcExpressionMenuAsset = Menu;
                TreePath.RemoveAll(tree => tree == null);
                MenuPath.RemoveAll(menu => menu == null);
                hub._navigationPath = TreePath;
                hub._menuNavigationPath = MenuPath;
                hub._mainScrollPos = Scroll;
                hub._animationToolClipsDirty = true;
                if (Dock != null)
                {
                    try
                    {
                        var panes = Dock.GetType().GetField("m_Panes", ToggleRecorderFlags)?.GetValue(Dock) as List<EditorWindow>;
                        var add = Dock.GetType().GetMethod("AddTab", ToggleRecorderFlags, null,
                            new[] { typeof(int), typeof(EditorWindow), typeof(bool) }, null);
                        if (panes != null && add != null)
                            add.Invoke(Dock, new object[] { Mathf.Clamp(TabIndex, 0, panes.Count), hub, true });
                    }
                    catch (Exception exception) { Debug.LogWarning("Avatar Control Hub: reopening in a floating window. " + exception.Message); }
                }
                hub.Show();
                if (!hub.docked) hub.position = Position;
                hub.Focus();
                if (Maximized) hub.maximized = true;
                hub.Repaint();
            }
        }

        [InitializeOnLoadMethod]
        private static void InitializeRecordingHubVisibility()
        {
            // The watcher also handles Unity's Record button when the Hub itself is closed.
            try
            {
                var method = typeof(AnimationMode).GetMethod("InAnimationRecording",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                _isAnimationRecording = method == null ? null : (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), method);
            }
            catch (Exception) { _isAnimationRecording = null; }
            EditorApplication.update -= UpdateRecordingHubVisibility;
            EditorApplication.update += UpdateRecordingHubVisibility;
            AssemblyReloadEvents.beforeAssemblyReload -= OnRecordingHubAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnRecordingHubAssemblyReload;
            EditorApplication.quitting -= OnRecordingHubQuitting;
            EditorApplication.quitting += OnRecordingHubQuitting;
            EditorApplication.playModeStateChanged -= OnToggleMakerPlayModeChanged;
            EditorApplication.playModeStateChanged += OnToggleMakerPlayModeChanged;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving -= OnToggleMakerSceneSaving;
            UnityEditor.SceneManagement.EditorSceneManager.sceneSaving += OnToggleMakerSceneSaving;
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosing -= OnToggleMakerSceneClosing;
            UnityEditor.SceneManagement.EditorSceneManager.sceneClosing += OnToggleMakerSceneClosing;
        }

        private static void UpdateRecordingHubVisibility()
        {
            if (_recordingEditorQuitting) return;
            UpdateToggleMakerRecording();
            bool recording = false;
            if (AnimationMode.InAnimationMode())
            {
                if (_isAnimationRecording != null) recording = _isAnimationRecording();
                else
                    foreach (var window in Resources.FindObjectsOfTypeAll<AnimationWindow>())
                        if (window.recording) { recording = true; break; }
            }
            if (recording == _hubObservedRecording) return;
            _hubObservedRecording = recording;
            if (!recording)
            {
                StopToggleMakerRecording();
                RestoreRecordingHubWindows();
                return;
            }

            _recordingClosedHubWindows.Clear();
            var parentField = typeof(EditorWindow).GetField("m_Parent", ToggleRecorderFlags);
            foreach (var hub in Resources.FindObjectsOfTypeAll<BlendTreeWindow>())
            {
                if (hub == null) continue;
                var host = parentField?.GetValue(hub) as UnityEngine.Object;
                if (host == null) continue; // A constructed but unopened window must not be reopened later.
                try
                {
                    var saved = new RecordingHubWindowState(hub, host);
                    hub.Close();
                    if (hub == null) _recordingClosedHubWindows.Add(saved);
                }
                catch (Exception exception) { Debug.LogWarning("Avatar Control Hub: could not close for recording. " + exception.Message); }
            }
        }

        private static void RestoreRecordingHubWindows()
        {
            if (_recordingClosedHubWindows.Count == 0) return;
            var savedWindows = _recordingClosedHubWindows.ToArray();
            _recordingClosedHubWindows.Clear();
            if (_recordingEditorQuitting) return;
            // Respect a Hub the user has already reopened manually; do not replace its choices or duplicate it.
            var parentField = typeof(EditorWindow).GetField("m_Parent", ToggleRecorderFlags);
            foreach (var hub in Resources.FindObjectsOfTypeAll<BlendTreeWindow>())
                if (hub != null && parentField?.GetValue(hub) is UnityEngine.Object host && host != null) return;
            foreach (var saved in savedWindows)
            {
                try { saved.Reopen(); }
                catch (Exception exception) { Debug.LogWarning("Avatar Control Hub: could not reopen after recording. " + exception.Message); }
            }
        }

        private static void OnRecordingHubAssemblyReload()
        {
            StopToggleMakerRecording();
            _hubObservedRecording = false;
            RestoreRecordingHubWindows();
        }

        private static void OnRecordingHubQuitting()
        {
            _recordingEditorQuitting = true;
            StopToggleMakerRecording();
            _recordingClosedHubWindows.Clear();
        }

        private static void HideToggleMakerOtherAvatars()
        {
            foreach (var descriptor in Resources.FindObjectsOfTypeAll<VRCAvatarDescriptor>())
            {
                if (descriptor == null) continue;
                var other = descriptor.gameObject;
                if (!other.scene.IsValid() || !other.scene.isLoaded || EditorUtility.IsPersistent(other) ||
                    !other.activeInHierarchy || UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(other) != null) continue;
                // Do not disable the recording hierarchy or any parent which keeps it active.
                if (other.transform.IsChildOf(_toggleRecordingAvatar.transform) ||
                    _toggleRecordingAvatar.transform.IsChildOf(other.transform) ||
                    other.transform.IsChildOf(_toggleRecordingCopy.transform) ||
                    _toggleRecordingCopy.transform.IsChildOf(other.transform)) continue;
                if (_toggleHiddenAvatars.ContainsKey(other)) continue;
                _toggleHiddenAvatars.Add(other, new ToggleMakerPose(other.transform));
                other.SetActive(false);
            }
        }

        private static void RestoreToggleMakerOtherAvatars()
        {
            foreach (var entry in _toggleHiddenAvatars)
            {
                try
                {
                    if (entry.Key == null) continue;
                    // Only avatars which were active when recording began are in this collection.
                    entry.Key.SetActive(true);
                    entry.Value.Apply(entry.Key.transform);
                }
                catch (Exception exception) { Debug.LogWarning("Animations Recorder avatar visibility restore: " + exception.Message); }
            }
            _toggleHiddenAvatars.Clear();
        }

        private void StartToggleMakerRecording()
        {
            if (_toggleRecording || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var avatar = _avatarRoot;
            if (avatar == null || EditorUtility.IsPersistent(avatar) || !avatar.scene.IsValid() || !avatar.scene.isLoaded ||
                UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(avatar) != null)
            {
                EditorUtility.DisplayDialog("Animations Recorder", "Assign an avatar instance from the scene as Avatar Root.", "OK");
                return;
            }
            if (avatar.transform.parent != null && !avatar.transform.parent.gameObject.activeInHierarchy)
            {
                EditorUtility.DisplayDialog("Animations Recorder", "Enable the avatar's parent objects before recording.", "OK");
                return;
            }
            if (AnimationMode.InAnimationMode())
            {
                EditorUtility.DisplayDialog("Animations Recorder", "Stop the current animation preview or recording before starting another.", "OK");
                return;
            }
            if (_AnimationRecorderClip == null && !CreateToggleMakerAnimation()) return;
            if (!IsToggleMakerClipEditable(_AnimationRecorderClip))
            {
                EditorUtility.DisplayDialog("Animations Recorder", "Assign an editable .anim file from Assets, or use New Animation, then press Record.", "OK");
                return;
            }
            try
            {
                var window = GetWindow<AnimationWindow>();
                var state = GetToggleRecorderState(window);
                var selectionProperty = state.GetType().GetProperty("selection", ToggleRecorderFlags);
                var itemType = typeof(AnimationWindow).Assembly.GetType("UnityEditorInternal.GameObjectSelectionItem");
                var createItem = itemType?.GetMethod("Create", System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(GameObject) }, null);
                if (selectionProperty == null || !selectionProperty.CanWrite || createItem == null)
                    throw new InvalidOperationException("This Unity version does not support the recording selection controls.");

                _toggleRecording = true;
                _toggleRecordingAvatar = avatar;
                _toggleAvatarWasActive = avatar.activeSelf;
                _toggleRecordingPose = new ToggleMakerPose(avatar.transform);
                _toggleRecordingClip = _AnimationRecorderClip;
                _toggleRecordingWindow = window;
                _toggleRecordingWindowState = state;
                _togglePreviousSelection = Selection.objects;
                var oldSelection = selectionProperty.GetValue(state) as UnityEngine.Object;
                if (oldSelection != null) _togglePreviousWindowSelection = Instantiate(oldSelection);

                // Clone while inactive so edit-mode scripts cannot initialize on an active copy.
                avatar.SetActive(false);
                _toggleRecordingCopy = Instantiate(avatar, avatar.transform.parent, false);
                _toggleRecordingCopy.name = avatar.name + " (Animations Recorder Recording Copy)";
                if (_toggleRecordingCopy.scene != avatar.scene)
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(_toggleRecordingCopy, avatar.scene);
                _toggleRecordingCopy.tag = "EditorOnly";
                foreach (var transform in _toggleRecordingCopy.GetComponentsInChildren<Transform>(true))
                {
                    transform.gameObject.hideFlags = HideFlags.DontSave;
                    foreach (var component in transform.GetComponents<Component>())
                        if (component != null) component.hideFlags = HideFlags.DontSave;
                }
                foreach (var animator in _toggleRecordingCopy.GetComponentsInChildren<Animator>(true)) DestroyImmediate(animator);
                foreach (var animation in _toggleRecordingCopy.GetComponentsInChildren<Animation>(true)) DestroyImmediate(animation);
                foreach (var behaviour in _toggleRecordingCopy.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null) continue;
                    bool furyComponent = false;
                    for (Type type = behaviour.GetType(); type != null; type = type.BaseType)
                        if (type.FullName == "VF.Component.VRCFuryComponent") { furyComponent = true; break; }
                    if (furyComponent) DestroyImmediate(behaviour);
                    else behaviour.enabled = false;
                }
                var recorderAnimator = _toggleRecordingCopy.AddComponent<Animator>();
                recorderAnimator.hideFlags = HideFlags.DontSave;
                recorderAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                // A humanoid Avatar makes preview retarget/reset the FBX skeleton, even for an empty clip.
                // Like VRCFury's recorder, use an Animator without an Avatar so only clip bindings animate.
                recorderAnimator.avatar = null;
                recorderAnimator.applyRootMotion = false;
                var controller = new AnimatorController { hideFlags = HideFlags.DontSave };
                _toggleRecordingTemporaryObjects.Add(controller);
                controller.AddLayer("Animations Recorder Recording");
                var machine = controller.layers[0].stateMachine;
                machine.hideFlags = HideFlags.DontSave;
                _toggleRecordingTemporaryObjects.Add(machine);
                var recordingState = machine.AddState("Animation");
                recordingState.hideFlags = HideFlags.DontSave;
                recordingState.writeDefaultValues = false;
                _toggleRecordingTemporaryObjects.Add(recordingState);
                recordingState.motion = _toggleRecordingClip;
                machine.defaultState = recordingState;
                recorderAnimator.runtimeAnimatorController = controller;
                _toggleRecordingCopy.SetActive(true);
                _toggleRecordingPose.Apply(_toggleRecordingCopy.transform);
                Selection.activeGameObject = _toggleRecordingCopy;
                window.Focus();
                var item = createItem.Invoke(null, new object[] { _toggleRecordingCopy }) as UnityEngine.Object;
                if (item == null) throw new InvalidOperationException("Could not select the recording copy in the Animation window.");
                try { selectionProperty.SetValue(state, item); }
                finally { DestroyImmediate(item); }
                window.animationClip = _toggleRecordingClip;
                window.time = 0f;
                if (!window.canRecord) throw new InvalidOperationException("Unity could not record this animation on the avatar copy.");
                HideToggleMakerOtherAvatars();
                window.recording = true;
                if (!window.recording) throw new InvalidOperationException("Unity did not enter animation recording mode.");
                window.Repaint();
                SceneView.RepaintAll();
                Repaint();
                UpdateRecordingHubVisibility();
            }
            catch (Exception exception)
            {
                StopToggleMakerRecording();
                EditorUtility.DisplayDialog("Animations Recorder", "Could not start recording. The original avatar has been restored.\n\n" +
                    (exception.InnerException?.Message ?? exception.Message), "OK");
            }
        }

        private static void UpdateToggleMakerRecording()
        {
            if (!_toggleRecording) return;
            try
            {
                if (_toggleRecordingAvatar == null || _toggleRecordingCopy == null ||
                    _toggleRecordingClip == null || _toggleRecordingWindow == null ||
                    !_toggleRecordingWindow.recording || _toggleRecordingWindow.animationClip != _toggleRecordingClip ||
                    GetToggleRecorderRoot(_toggleRecordingWindowState) != _toggleRecordingCopy)
                    StopToggleMakerRecording();
            }
            catch (Exception) { StopToggleMakerRecording(); }
        }

        private static void OnToggleMakerPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) StopToggleMakerRecording();
        }

        private static void OnToggleMakerSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            if (_toggleRecording && ToggleMakerAffectsScene(scene)) StopToggleMakerRecording();
        }

        private static void OnToggleMakerSceneClosing(UnityEngine.SceneManagement.Scene scene, bool removingScene)
        {
            if (_toggleRecording && ToggleMakerAffectsScene(scene)) StopToggleMakerRecording();
        }

        private static bool ToggleMakerAffectsScene(UnityEngine.SceneManagement.Scene scene)
        {
            if (_toggleRecordingAvatar == null || _toggleRecordingAvatar.scene == scene) return true;
            foreach (var avatar in _toggleHiddenAvatars.Keys)
                if (avatar != null && avatar.scene == scene) return true;
            return false;
        }

        private static void StopToggleMakerRecording()
        {
            if (!_toggleRecording) return;
            _toggleRecording = false;
            var avatar = _toggleRecordingAvatar;
            var copy = _toggleRecordingCopy;
            var clip = _toggleRecordingClip;
            var window = _toggleRecordingWindow;
            bool restoreSelection = Selection.activeGameObject == null || (copy != null &&
                (Selection.activeGameObject == copy || Selection.activeGameObject.transform.IsChildOf(copy.transform)));
            try
            {
                var recordingRoot = GetToggleRecorderRoot(_toggleRecordingWindowState);
                bool ownsWindowSelection = copy != null ? recordingRoot == copy :
                    recordingRoot == null && clip != null && window != null && window.animationClip == clip;
                if (window != null && ownsWindowSelection)
                {
                    window.recording = false;
                    window.previewing = false;
                    _toggleRecordingWindowState.GetType().GetProperty("selection", ToggleRecorderFlags)?.SetValue(
                        _toggleRecordingWindowState, _togglePreviousWindowSelection);
                    window.Repaint();
                }
            }
            catch (Exception exception) { Debug.LogWarning("Animations Recorder: could not reset the Animation window. " + exception.Message); }
            finally
            {
                // Each cleanup step is independent, so a broken preview cannot leave the avatar hidden.
                try { if (copy != null) DestroyImmediate(copy); }
                catch (Exception exception) { Debug.LogWarning("Animations Recorder copy cleanup: " + exception.Message); }
                try
                {
                    if (avatar != null)
                    {
                        if (avatar.activeSelf != _toggleAvatarWasActive) avatar.SetActive(_toggleAvatarWasActive);
                        _toggleRecordingPose?.Apply(avatar.transform);
                    }
                }
                catch (Exception exception) { Debug.LogWarning("Animations Recorder avatar restore: " + exception.Message); }
                RestoreToggleMakerOtherAvatars();
                for (int i = _toggleRecordingTemporaryObjects.Count - 1; i >= 0; i--)
                {
                    var temporary = _toggleRecordingTemporaryObjects[i];
                    try { if (temporary != null && !AssetDatabase.Contains(temporary)) DestroyImmediate(temporary); }
                    catch (Exception exception) { Debug.LogWarning("Animations Recorder cleanup: " + exception.Message); }
                }
                _toggleRecordingTemporaryObjects.Clear();
                try { if (_togglePreviousWindowSelection != null) DestroyImmediate(_togglePreviousWindowSelection); }
                catch (Exception exception) { Debug.LogWarning("Animations Recorder selection cleanup: " + exception.Message); }
                _togglePreviousWindowSelection = null;
                _toggleRecordingCopy = null;
                _toggleRecordingAvatar = null;
                _toggleRecordingPose = null;
                _toggleRecordingClip = null;
                _toggleRecordingWindow = null;
                _toggleRecordingWindowState = null;
            }
            try
            {
                if (restoreSelection)
                {
                    var previous = new List<UnityEngine.Object>(_togglePreviousSelection ?? Array.Empty<UnityEngine.Object>());
                    previous.RemoveAll(item => item == null);
                    Selection.objects = previous.ToArray();
                    if (previous.Count == 0 && avatar != null) Selection.activeGameObject = avatar;
                }
            }
            catch (Exception exception) { Debug.LogWarning("Animations Recorder selection restore: " + exception.Message); }
            finally { _togglePreviousSelection = null; }
            try
            {
                if (clip != null)
                {
                    EditorUtility.SetDirty(clip);
                    AssetDatabase.SaveAssetIfDirty(clip);
                }
            }
            catch (Exception exception) { Debug.LogWarning("Animations Recorder: the avatar was restored, but the animation could not be saved. " + exception.Message); }
            foreach (var hub in Resources.FindObjectsOfTypeAll<BlendTreeWindow>())
            {
                hub._animationToolClipsDirty = true;
                if (hub._hasSetupScan) hub._setupChecksStale = true;
                hub.Repaint();
            }
            SceneView.RepaintAll();
        }

        private void DrawHeader()
        {
            GUILayout.Space(4);
            float lineHeight = EditorGUIUtility.singleLineHeight;
            float fieldOffset = lineHeight + EditorGUIUtility.standardVerticalSpacing;
            float slotHeight = fieldOffset + lineHeight;
            int columns = position.width >= 800f ? 4 : 2;
            int rows = 4 / columns;
            Rect headerRect = EditorGUILayout.GetControlRect(false, rows * slotHeight + (rows - 1) * 4f);
            const float buttonWidth = 160f;
            const float gap = 8f;
            float slotWidth = Mathf.Max(1f, (headerRect.width - buttonWidth - gap * columns) / columns);
            Rect buttonRect = new Rect(headerRect.x, headerRect.y + fieldOffset, buttonWidth, lineHeight);
            if (GUI.Button(buttonRect, new GUIContent("Use Selected Avatar",
                "Select your avatar or one of its children in the Hierarchy, then load its root, custom FX controller, VRC parameters and menu.")))
            {
                LoadSelectedAvatarAssets();
            }

            Rect slotRect = new Rect(headerRect.x + buttonWidth + gap, headerRect.y, slotWidth, slotHeight);
            EditorGUI.BeginChangeCheck();
            GameObject newAvatarRoot = (GameObject)DrawHeaderAssetSlot(slotRect, "Avatar Root", _avatarRoot, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) SetAvatarRoot(newAvatarRoot);

            slotRect.x += slotWidth + gap;
            EditorGUI.BeginChangeCheck();

            UnityEngine.Object newTarget = DrawHeaderAssetSlot(slotRect, "Animation Controller", _targetAsset, typeof(RuntimeAnimatorController));

            if (EditorGUI.EndChangeCheck())
            {
                GUI.FocusControl(null);
                if (newTarget == null || newTarget is RuntimeAnimatorController || newTarget is BlendTree)
                {
                    _targetAsset = newTarget;

                    if (_targetAsset != null)
                    {
                        string path = AssetDatabase.GetAssetPath(_targetAsset);
                        EditorPrefs.SetString(GetProjectScopedPreferenceKey(PrefLastAssetKey), path);
                    }
                    else
                    {
                        EditorPrefs.DeleteKey(GetProjectScopedPreferenceKey(PrefLastAssetKey));
                    }

                    RefreshAvailableTrees();
                    if (_hideVrcBuiltInParameters)
                    {
                        RemoveAnimatorOnlyParametersFromVrc();
                        EnsureBlendTreeWeightFloatParameter(GetAnimatorController());
                    }
                }
                else
                {
                    EditorUtility.DisplayDialog("Invalid Asset", "Please drag in an Animator Controller or a Blend Tree.", "OK");
                }
            }

            slotRect.x = headerRect.x + buttonWidth + gap + (2 % columns) * (slotWidth + gap);
            slotRect.y = headerRect.y + (2 / columns) * (slotHeight + 4f);
            EditorGUI.BeginChangeCheck();
            VRCExpressionParameters newParamsAsset = (VRCExpressionParameters)DrawHeaderAssetSlot(slotRect, "VRC Parameters", _vrcExpressionParametersAsset, typeof(VRCExpressionParameters));
            if (EditorGUI.EndChangeCheck())
            {
                _vrcExpressionParametersAsset = newParamsAsset;
                if (_vrcExpressionParametersAsset != null)
                {
                    EditorPrefs.SetString(GetProjectScopedPreferenceKey(PrefExpressionParametersKey), AssetDatabase.GetAssetPath(_vrcExpressionParametersAsset));
                }
                else
                {
                    EditorPrefs.DeleteKey(GetProjectScopedPreferenceKey(PrefExpressionParametersKey));
                }

                RemoveAnimatorOnlyParametersFromVrc();
                if (_hideVrcBuiltInParameters)
                {
                    EnsureBlendTreeWeightFloatParameter(GetAnimatorController());
                }
            }

            slotRect.x += slotWidth + gap;
            EditorGUI.BeginChangeCheck();
            VRCExpressionsMenu newMenuAsset = (VRCExpressionsMenu)DrawHeaderAssetSlot(slotRect, "VRC Menu Root", _vrcExpressionMenuAsset, typeof(VRCExpressionsMenu));
            if (EditorGUI.EndChangeCheck())
            {
                _vrcExpressionMenuAsset = newMenuAsset;
                if (_vrcExpressionMenuAsset != null)
                {
                    EditorPrefs.SetString(GetProjectScopedPreferenceKey(PrefExpressionMenuKey), AssetDatabase.GetAssetPath(_vrcExpressionMenuAsset));
                }
                else
                {
                    EditorPrefs.DeleteKey(GetProjectScopedPreferenceKey(PrefExpressionMenuKey));
                }
            }

            GUILayout.Space(4);
            DrawSectionVisibilityToggles();
            GUILayout.Space(2);
            GuiLine(1);
        }

        private static readonly GUIContent[] SectionVisibilityLabels =
        {
            new GUIContent("Avatar Setup"),
            new GUIContent("Parameters Manager"),
            new GUIContent("VRC Menu Editor"),
            new GUIContent("Layer Editor"),
            new GUIContent("BlendTree Editor"),
            new GUIContent("Animations Recorder"),
            new GUIContent("Blendshape Mapper"),
            new GUIContent("Animation Path Replacer"),
            new GUIContent("Merger")
        };

        private static readonly string[] SectionVisibilityPreferenceKeys =
        {
            PrefShowSetupChecksKey, PrefShowParamManagerKey, PrefShowMenuEditorKey,
            PrefShowLayerEditorKey, PrefShowBlendTreeEditorKey, PrefShowToggleMakerKey,
            PrefShowBlendshapeMapperKey, PrefShowPathReplaceEditorKey,
            PrefShowAnimatorLayerMergerKey
        };

        private void DrawSectionVisibilityToggles()
        {
            bool[] visibility = { _showSetupChecks, _showParamManager, _showMenuEditor, _showLayerEditor, _showBlendTreeEditor,
                _showToggleMaker, _showBlendshapeMapper, _showPathReplaceEditor, _showAnimatorLayerMerger };
            float[] widths = new float[SectionVisibilityLabels.Length];
            const float gap = 12f;
            float availableWidth = Mathf.Max(1f, position.width - 8f);
            float rowWidth = 0f;
            int rowCount = 1;
            for (int i = 0; i < widths.Length; i++)
            {
                widths[i] = EditorStyles.toggle.CalcSize(SectionVisibilityLabels[i]).x + 4f;
                if (i > 0 && rowWidth + gap + widths[i] > availableWidth)
                {
                    rowCount++;
                    rowWidth = 0f;
                }
                rowWidth += (rowWidth > 0f ? gap : 0f) + widths[i];
            }

            float lineHeight = EditorGUIUtility.singleLineHeight;
            float rowSpacing = EditorGUIUtility.standardVerticalSpacing;
            Rect area = EditorGUILayout.GetControlRect(false, rowCount * lineHeight + (rowCount - 1) * rowSpacing);
            float x = area.x;
            float y = area.y;
            bool changed = false;
            for (int i = 0; i < visibility.Length; i++)
            {
                if (i > 0 && x - area.x + widths[i] > availableWidth)
                {
                    x = area.x;
                    y += lineHeight + rowSpacing;
                }
                bool visible = GUI.Toggle(new Rect(x, y, widths[i], lineHeight), visibility[i], SectionVisibilityLabels[i], EditorStyles.toggle);
                if (visible != visibility[i])
                {
                    visibility[i] = visible;
                    EditorPrefs.SetBool(SectionVisibilityPreferenceKeys[i], visible);
                    changed = true;
                }
                x += widths[i] + gap;
            }

            _showSetupChecks = visibility[0];
            _showParamManager = visibility[1];
            _showMenuEditor = visibility[2];
            _showLayerEditor = visibility[3];
            _showBlendTreeEditor = visibility[4];
            _showToggleMaker = visibility[5];
            _showBlendshapeMapper = visibility[6];
            _showPathReplaceEditor = visibility[7];
            _showAnimatorLayerMerger = visibility[8];
            if (changed)
            {
                GUI.FocusControl(null);
                Repaint();
            }
        }

        private static UnityEngine.Object DrawHeaderAssetSlot(Rect rect, string label, UnityEngine.Object asset, Type assetType, bool allowSceneObjects = false)
        {
            float lineHeight = EditorGUIUtility.singleLineHeight;
            Rect labelRect = new Rect(rect.x, rect.y, rect.width, lineHeight);
            Rect fieldRect = new Rect(rect.x, rect.y + lineHeight + EditorGUIUtility.standardVerticalSpacing, rect.width, lineHeight);
            EditorGUI.LabelField(labelRect, new GUIContent(label, label), EditorStyles.miniLabel);
            return EditorGUI.ObjectField(fieldRect, asset, assetType, allowSceneObjects);
        }

        private void SetAvatarRoot(GameObject root)
        {
            _avatarRoot = root;
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
            {
                string key = GetAvatarRootPreferenceKey();
                // An explicit clear or a new unsaved selection replaces the previous choice.
                EditorPrefs.SetString(key, "");
                SessionState.SetInt(key, 0);
                SaveProjectAvatarRoot();
            }
            if (_hasSetupScan) _setupChecksStale = true;
            Repaint();
        }

        private string GetAvatarRootPreferenceKey()
        {
            if (string.IsNullOrEmpty(_avatarRootPreferenceKey))
                _avatarRootPreferenceKey = GetProjectScopedPreferenceKey(PrefAvatarRootKey);
            return _avatarRootPreferenceKey;
        }

        private void LoadProjectAvatarRoot()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            string key = GetAvatarRootPreferenceKey();
            if (!EditorPrefs.HasKey(key))
            {
                // Migrate this project's existing window selection once.
                EditorPrefs.SetString(key, "");
                SaveProjectAvatarRoot();
            }
            else
            {
                _avatarRoot = null;
                RestoreProjectAvatarRoot();
            }
        }

        private void SaveProjectAvatarRoot()
        {
            // A missing object can mean its scene is closed. Only SetAvatarRoot clears a choice.
            if (_avatarRoot == null || EditorApplication.isPlayingOrWillChangePlaymode ||
                UnityEditor.SceneManagement.EditorSceneManager.IsPreviewSceneObject(_avatarRoot)) return;
            string key = GetAvatarRootPreferenceKey();
            SessionState.SetInt(key, _avatarRoot.GetInstanceID());
            if (!EditorUtility.IsPersistent(_avatarRoot) &&
                (!_avatarRoot.scene.IsValid() || string.IsNullOrEmpty(_avatarRoot.scene.path))) return;
            GlobalObjectId id = GlobalObjectId.GetGlobalObjectIdSlow(_avatarRoot);
            if (id.identifierType == 0 || id.targetObjectId == 0 || id.assetGUID == default(GUID)) return;
            string value = id.ToString();
            if (EditorPrefs.GetString(key, "") != value) EditorPrefs.SetString(key, value);
        }

        private void RestoreProjectAvatarRoot()
        {
            if (_avatarRoot != null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            string key = GetAvatarRootPreferenceKey();
            string saved = EditorPrefs.GetString(key, "");
            // SessionState also retains selections in an unsaved scene through script reloads.
            int sessionId = SessionState.GetInt(key, 0);
            var sessionRoot = sessionId != 0 ? EditorUtility.InstanceIDToObject(sessionId) as GameObject : null;
            if (sessionRoot != null && !UnityEditor.SceneManagement.EditorSceneManager.IsPreviewSceneObject(sessionRoot) &&
                (string.IsNullOrEmpty(saved) || GlobalObjectId.GetGlobalObjectIdSlow(sessionRoot).ToString() == saved))
                _avatarRoot = sessionRoot;
            if (_avatarRoot == null && !string.IsNullOrEmpty(saved) && GlobalObjectId.TryParse(saved, out var id))
            {
                if (id.identifierType == 2)
                {
                    string scenePath = AssetDatabase.GUIDToAssetPath(id.assetGUID.ToString());
                    if (string.IsNullOrEmpty(scenePath)) return;
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
                    if (!scene.IsValid() || !scene.isLoaded) return;
                }
                _avatarRoot = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GameObject;
            }
            if (_avatarRoot == null) return;
            SessionState.SetInt(key, _avatarRoot.GetInstanceID());
            if (_hasSetupScan) _setupChecksStale = true;
            Repaint();
        }

        private void OnAvatarRootSceneOpened(UnityEngine.SceneManagement.Scene scene, UnityEditor.SceneManagement.OpenSceneMode mode)
        {
            RestoreProjectAvatarRoot();
        }

        private void OnAvatarRootSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            if (_avatarRoot != null && _avatarRoot.scene == scene) SaveProjectAvatarRoot();
            else RestoreProjectAvatarRoot();
        }

        private void OnAvatarRootPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                _avatarRoot = null;
                LoadProjectAvatarRoot();
            }
        }

        private static void SaveProjectAssetSlot(string key, UnityEngine.Object asset)
        {
            string scopedKey = GetProjectScopedPreferenceKey(key);
            string path = asset != null ? AssetDatabase.GetAssetPath(asset) : "";
            if (string.IsNullOrEmpty(path)) EditorPrefs.DeleteKey(scopedKey);
            else EditorPrefs.SetString(scopedKey, path);
        }

        private static VRCAvatarDescriptor GetSelectedSetupAvatar()
        {
            GameObject selected = Selection.activeGameObject;
            VRCAvatarDescriptor avatar = selected != null ? selected.GetComponentInParent<VRCAvatarDescriptor>(true) : null;
            if (avatar == null && selected != null)
            {
                var avatars = selected.GetComponentsInChildren<VRCAvatarDescriptor>(true);
                if (avatars.Length == 1) avatar = avatars[0];
            }
            return avatar;
        }

        private void LoadSelectedAvatarAssets()
        {
            VRCAvatarDescriptor avatar = GetSelectedSetupAvatar();
            if (avatar == null)
            {
                EditorUtility.DisplayDialog("Select an Avatar",
                    "Select one avatar, or an object inside it, in the Hierarchy and try again.", "OK");
                return;
            }

            RuntimeAnimatorController fxController = null;
            if (avatar.customizeAnimationLayers && avatar.baseAnimationLayers != null)
            {
                foreach (var layer in avatar.baseAnimationLayers)
                {
                    if (layer.type == VRCAvatarDescriptor.AnimLayerType.FX && !layer.isDefault)
                    {
                        if (fxController != null && layer.animatorController != null && fxController != layer.animatorController)
                        {
                            EditorUtility.DisplayDialog("Multiple FX Controllers",
                                "This avatar has more than one custom FX controller. Choose the controller manually in the slot above.", "OK");
                            return;
                        }
                        if (layer.animatorController != null) fxController = layer.animatorController;
                    }
                }
            }

            CancelMenuDrag();
            SetAvatarRoot(avatar.gameObject);
            _targetAsset = fxController;
            _vrcExpressionParametersAsset = avatar.customExpressions ? avatar.expressionParameters : null;
            _vrcExpressionMenuAsset = avatar.customExpressions ? avatar.expressionsMenu : null;
            SaveProjectAssetSlot(PrefLastAssetKey, _targetAsset);
            SaveProjectAssetSlot(PrefExpressionParametersKey, _vrcExpressionParametersAsset);
            SaveProjectAssetSlot(PrefExpressionMenuKey, _vrcExpressionMenuAsset);

            _menuNavigationPath.Clear();
            _selectedMenuControlIndex = -1;
            ResetMenuControlSelection();
            _menuSearchQuery = "";
            _nextMenuSearchRefresh = 0;
            _selectedParamIndex = -1;
            _selectedLayerIndex = -1;
            _layerSelectionStateMachine = null;
            _layerSelectionName = null;
            ResetStateMachineNavigation();
            InvalidateLayerSummaries();
            _selectedState = null;
            _selectedTransition = null;
            _selectedIncomingTransition = null;
            _hasParamCache = false;
            _hasSetupScan = false;
            _setupIssues.Clear();
            _setupAssetVersions.Clear();
            _mainScrollPos = Vector2.zero;
            if (_cachedBehaviourEditor != null)
            {
                DestroyImmediate(_cachedBehaviourEditor);
                _cachedBehaviourEditor = null;
            }
            RefreshAvailableTrees();
            if (_hideVrcBuiltInParameters)
            {
                RemoveAnimatorOnlyParametersFromVrc();
                EnsureBlendTreeWeightFloatParameter(GetAnimatorController());
            }
            GUI.FocusControl(null);
            ShowNotification(new GUIContent("Loaded slots from " + avatar.name));
        }

        private void AddSetupIssue(MessageType severity, string message, UnityEngine.Object asset, GameObject[] avatarObjects = null,
            Component[] avatarComponents = null, Action autoFix = null, List<ParameterUsageEntry> locations = null, string locationId = null,
            List<SetupMissingParameter> missingParameters = null, List<SetupMissingScript> missingScripts = null,
            bool writeDefaultsOptions = false)
        {
            _setupIssues.Add(new SetupIssue { severity = severity, message = message, asset = asset,
                avatarObjects = avatarObjects, avatarComponents = avatarComponents, autoFix = autoFix,
                locations = locations, locationId = locationId, missingParameters = missingParameters, missingScripts = missingScripts,
                writeDefaultsOptions = writeDefaultsOptions });
        }

        private void ShowSetupMissingScripts(SetupIssue issue)
        {
            var menu = new HubDropdownMenu(this);
            menu.AddItem(new GUIContent("Show all affected objects"), false, () => ShowSetupIssue(issue),
                enabledNow: () => Array.Exists(issue.avatarObjects, owner => owner != null));
            menu.AddSeparator("");
            foreach (var script in issue.missingScripts)
            {
                var label = new GUIContent(script.objectPath + " — Missing script (component " + (script.componentIndex + 1) + ")",
                    "Select this object in the Hierarchy to inspect its missing component.");
                menu.items.Add(new HubDropdownItem
                {
                    content = label,
                    action = () => ShowSetupIssue(new SetupIssue { avatarObjects = new[] { script.owner } }),
                    enabledNow = () => script.owner != null,
                    buttonContent = new GUIContent("Remove", "Remove only this missing component. Supports Undo. Use an avatar in the scene or Prefab Mode."),
                    buttonAction = () => EditorApplication.delayCall += () => { if (this != null) RemoveSetupMissingScripts(issue, script); },
                    buttonEnabledNow = () => CanRemoveSetupMissingScripts(issue) && script.owner != null
                });
            }
            menu.ShowAsContext();
        }

        private bool CanRemoveSetupMissingScripts(SetupIssue issue)
        {
            var root = issue.asset as GameObject;
            return root != null && root == _avatarRoot && !EditorUtility.IsPersistent(root) &&
                !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling &&
                issue.missingScripts != null && issue.missingScripts.Count > 0;
        }

        private static int[] GetSetupComponentIds(GameObject owner)
        {
            using var serialized = new SerializedObject(owner);
            var slots = serialized.FindProperty("m_Component");
            if (slots == null || !slots.isArray) return null;
            var ids = new int[slots.arraySize];
            for (int i = 0; i < ids.Length; i++)
            {
                var reference = slots.GetArrayElementAtIndex(i).FindPropertyRelative("component");
                if (reference == null) return null;
                ids[i] = reference.objectReferenceInstanceIDValue;
            }
            return ids;
        }

        private static bool TryGetSetupMissingScriptTarget(SetupMissingScript script, GameObject root,
            Dictionary<MonoScript, bool> missingCache, out UnityEngine.Object target)
        {
            target = null;
            if (script.owner == null || EditorUtility.IsPersistent(script.owner) ||
                !script.owner.transform.IsChildOf(root.transform) ||
                (script.owner.hideFlags & HideFlags.NotEditable) != 0 || script.componentIds == null) return false;
            var components = script.owner.GetComponents<Component>();
            if (components.Length != script.componentIds.Length || script.componentIndex < 0 || script.componentIndex >= components.Length ||
                !IsSetupMissingScript(components[script.componentIndex], missingCache)) return false;
            using var serialized = new SerializedObject(script.owner);
            var slots = serialized.FindProperty("m_Component");
            if (slots == null || slots.arraySize != script.componentIds.Length) return false;
            // Validate the complete component list, so a stale index can never delete another entry.
            for (int i = 0; i < slots.arraySize; i++)
            {
                var reference = slots.GetArrayElementAtIndex(i).FindPropertyRelative("component");
                if (reference == null || reference.objectReferenceInstanceIDValue != script.componentIds[i]) return false;
            }
            var slot = slots.GetArrayElementAtIndex(script.componentIndex).FindPropertyRelative("component");
            // A zero-ID entry has no identity beyond its slot; require an unchanged owner in that case.
            if (slot.objectReferenceInstanceIDValue == 0 && EditorUtility.GetDirtyCount(script.owner) != script.ownerVersion) return false;
            target = components[script.componentIndex];
            if (target == null)
            {
                var serializedTarget = slot.objectReferenceValue;
                if (!ReferenceEquals(serializedTarget, null)) target = serializedTarget;
            }
            if (target == null && slot.objectReferenceInstanceIDValue != 0)
            {
                var nativeTarget = EditorUtility.InstanceIDToObject(slot.objectReferenceInstanceIDValue);
                if (!ReferenceEquals(nativeTarget, null)) target = nativeTarget;
            }
            if (target == null)
            {
                var indexedTarget = script.owner.GetComponentAtIndex(script.componentIndex);
                if (!ReferenceEquals(indexedTarget, null)) target = indexedTarget;
            }
            // Unity may expose a missing behaviour as a plain Object rather than a managed Component.
            // Validate its serialized owner instead of discarding that native deletion target.
            if (!ReferenceEquals(target, null))
            {
                try
                {
                    using var componentData = new SerializedObject(target);
                    var ownerProperty = componentData.FindProperty("m_GameObject");
                    if (target is Transform || ownerProperty == null || ownerProperty.objectReferenceValue != script.owner ||
                        componentData.FindProperty("m_Script") == null) target = null;
                }
                catch (Exception)
                {
                    // The slot is still validated; native missing-component cleanup can handle an inaccessible object.
                    target = null;
                }
            }
            return true;
        }

        private static int RemoveAllSetupMissingScriptComponents(GameObject root, string undoName)
        {
            int removed = 0;
            var missingCache = new Dictionary<MonoScript, bool>();
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                var owner = transform.gameObject;
                if (EditorUtility.IsPersistent(owner) || (owner.hideFlags & HideFlags.NotEditable) != 0) continue;
                var before = owner.GetComponents<Component>();
                var retainedTargets = new List<Component>();
                foreach (var component in before)
                    if (component != null && IsSetupMissingScript(component, missingCache)) retainedTargets.Add(component);
                int missingCount = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(owner);
                if (missingCount == 0 && retainedTargets.Count == 0) continue;
                Undo.RegisterCompleteObjectUndo(owner, undoName);
                // Missing native components often have no managed Object to pass to DestroyObjectImmediate.
                // Let Unity remove those component slots directly from this GameObject.
                if (missingCount > 0) GameObjectUtility.RemoveMonoBehavioursWithMissingScript(owner);
                // Also handle deleted script files whose old managed types are still loaded.
                foreach (var component in retainedTargets)
                    if (component != null && IsSetupMissingScript(component, missingCache)) Undo.DestroyObjectImmediate(component);
                int difference = before.Length - owner.GetComponents<Component>().Length;
                if (difference < 0 || difference > missingCount + retainedTargets.Count)
                    throw new InvalidOperationException("The component list changed unexpectedly while removing missing scripts.");
                removed += difference;
                if (difference > 0) EditorUtility.SetDirty(owner);
            }
            return removed;
        }

        private static int RemoveSingleSetupMissingScriptComponent(SetupMissingScript script, GameObject root, string undoName)
        {
            if (!TryGetSetupMissingScriptTarget(script, root, new Dictionary<MonoScript, bool>(), out var target))
                throw new InvalidOperationException("This component list changed. Scan Setup again before removing this entry.");
            var owner = script.owner;
            int before = owner.GetComponents<Component>().Length;
            if (!ReferenceEquals(target, null)) Undo.DestroyObjectImmediate(target);
            int difference = before - owner.GetComponents<Component>().Length;
            if (difference == 0)
            {
                // This fallback is safe only when the chosen slot is the sole missing native component.
                // Never turn an individual Remove click into removing other missing components on the object.
                var components = owner.GetComponents<Component>();
                if (components[script.componentIndex] == null && GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(owner) == 1)
                {
                    Undo.RegisterCompleteObjectUndo(owner, undoName);
                    GameObjectUtility.RemoveMonoBehavioursWithMissingScript(owner);
                    difference = before - owner.GetComponents<Component>().Length;
                }
            }
            if (difference != 1)
                throw new InvalidOperationException("Unity could not remove this component individually. Its GameObject has been left unchanged.");
            EditorUtility.SetDirty(owner);
            return difference;
        }

        private void RemoveSetupMissingScripts(SetupIssue issue, SetupMissingScript only = null)
        {
            if (!CanRemoveSetupMissingScripts(issue)) return;
            var root = (GameObject)issue.asset;
            if (only != null && !issue.missingScripts.Contains(only)) return;
            CloseHubDropdown();
            int removed = 0;
            string failure = null;
            string undoName = only != null ? "Remove Missing Script" : "Remove All Missing Scripts";
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            try
            {
                removed = only != null ? RemoveSingleSetupMissingScriptComponent(only, root, undoName) :
                    RemoveAllSetupMissingScriptComponents(root, undoName);
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(undoGroup);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                removed = 0;
                failure = exception.Message;
                Debug.LogWarning("Missing component removal: " + failure, root);
            }
            if (removed > 0 && root.scene.IsValid()) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.scene);
            RefreshSetupScan();
            string message = failure ?? (removed > 0 ? "Removed " + removed + " missing component" + (removed == 1 ? "." : "s.") :
                "No missing components were removed. Check the refreshed results.");
            ShowNotification(new GUIContent(message));
            Repaint();
        }

        private void ShowSetupLocations(SetupIssue issue)
        {
            var menu = new HubDropdownMenu(this);
            foreach (var location in issue.locations)
            {
                var content = new GUIContent(location.location, location.location);
                if (location.target == null) menu.AddDisabledItem(content);
                else menu.AddItem(content, false, () => EditorApplication.delayCall += () =>
                {
                    if (this == null || location.target == null) return;
                    ShowParameterUsage(location);
                });
            }
            menu.ShowAsContext();
        }

        private static SetupParameterTypes GetSetupParameterTypes(AnimatorControllerParameterType type)
        {
            switch (type)
            {
                case AnimatorControllerParameterType.Bool: return SetupParameterTypes.Bool;
                case AnimatorControllerParameterType.Int: return SetupParameterTypes.Int;
                case AnimatorControllerParameterType.Float: return SetupParameterTypes.Float;
                default: return SetupParameterTypes.None;
            }
        }

        private static SetupParameterTypes GetSetupConditionParameterTypes(AnimatorConditionMode mode)
        {
            switch (mode)
            {
                case AnimatorConditionMode.If:
                case AnimatorConditionMode.IfNot: return SetupParameterTypes.Bool;
                case AnimatorConditionMode.Equals:
                case AnimatorConditionMode.NotEqual: return SetupParameterTypes.Int;
                case AnimatorConditionMode.Greater:
                case AnimatorConditionMode.Less: return SetupParameterTypes.Numeric;
                default: return SetupParameterTypes.None;
            }
        }

        private static SetupParameterTypes GetSetupParameterValueHint(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return SetupParameterTypes.None;
            if (value == 0f || value == 1f) return SetupParameterTypes.Bool;
            if (value >= -1f && value <= 1f) return SetupParameterTypes.Float;
            return SetupParameterTypes.Int;
        }

        private static List<SetupMissingParameter> BuildSetupMissingParameterRows(AnimatorController controller,
            Dictionary<string, AnimatorControllerParameterType> animatorParameters,
            Dictionary<string, VRCExpressionParameters.Parameter> expressionParameters, List<ParameterUsageEntry> locations)
        {
            var rows = new List<SetupMissingParameter>();
            var byName = new Dictionary<string, SetupMissingParameter>(StringComparer.Ordinal);
            foreach (var location in locations)
            {
                // Keep unnamed references accessible inside Parameters, without offering Add.
                string name = string.IsNullOrWhiteSpace(location.missingParameterName) ? "" : location.missingParameterName;
                if (!byName.TryGetValue(name, out var row))
                {
                    row = new SetupMissingParameter { name = name,
                        missingFromAnimator = name.Length > 0 && controller != null && !animatorParameters.ContainsKey(name) };
                    byName.Add(name, row);
                    rows.Add(row);
                }
                row.allowedTypes &= location.missingParameterTypes;
                if (location.missingParameterValue.HasValue)
                    row.valueHints |= GetSetupParameterValueHint(location.missingParameterValue.Value);
                row.locations.Add(location);
            }
            foreach (var row in rows)
            {
                if (row.name.Length == 0)
                {
                    row.typeHint = "No parameter name is assigned. Use Show to open a reference and assign one.";
                    continue;
                }
                if (VrcBuiltInParameterTypes.TryGetValue(row.name, out var builtInType))
                    row.allowedTypes &= GetSetupParameterTypes(builtInType);
                if (row.allowedTypes == SetupParameterTypes.None)
                {
                    row.typeHint = "These references require incompatible parameter types. Use Show to correct the conflicting uses before adding the parameter.";
                    continue;
                }
                if (expressionParameters.TryGetValue(row.name, out var vrcParameter))
                {
                    var vrcType = GetAnimatorTypeForVrcType((int)vrcParameter.valueType);
                    if ((row.allowedTypes & GetSetupParameterTypes(vrcType)) != 0)
                    {
                        row.type = vrcType;
                        row.typeHint = "Type from the VRC Parameters list, compatible with the missing references.";
                    }
                }
                int options = (int)row.allowedTypes;
                if (row.type == null && (options & (options - 1)) == 0)
                {
                    row.type = row.allowedTypes == SetupParameterTypes.Bool ? AnimatorControllerParameterType.Bool :
                        row.allowedTypes == SetupParameterTypes.Int ? AnimatorControllerParameterType.Int : AnimatorControllerParameterType.Float;
                    row.typeHint = "Type required by the parameter's references.";
                }
                if (row.type == null && row.valueHints != SetupParameterTypes.None)
                {
                    // Consider every reference: a fractional/negative unit-range value outweighs
                    // binary values, and an outside-range value outweighs binary values too.
                    var suggestedType = (row.valueHints & SetupParameterTypes.Float) != 0 ? AnimatorControllerParameterType.Float :
                        (row.valueHints & SetupParameterTypes.Int) != 0 ? AnimatorControllerParameterType.Int : AnimatorControllerParameterType.Bool;
                    if ((row.allowedTypes & GetSetupParameterTypes(suggestedType)) != 0)
                    {
                        row.type = suggestedType;
                        row.typeHint = suggestedType == AnimatorControllerParameterType.Bool
                            ? "Suggested Bool because the referenced values are only 0 or 1. You can change this before adding."
                            : suggestedType == AnimatorControllerParameterType.Float
                            ? "Suggested Float because a referenced value is between -1 and 1 and is not 0 or 1. You can change this before adding."
                            : "Suggested Int because a referenced value is outside -1 to 1. You can change this before adding.";
                    }
                    else if (suggestedType == AnimatorControllerParameterType.Bool && (row.allowedTypes & SetupParameterTypes.Int) != 0)
                    {
                        row.type = AnimatorControllerParameterType.Int;
                        row.typeHint = "The referenced values are only 0 or 1, but the existing numeric comparisons require Int or Float. Suggested Int to keep those comparisons working.";
                    }
                }
                if (row.type == null)
                    row.typeHint = "The original type cannot be determined from these references. Choose a compatible type before adding it.";
            }
            return rows;
        }

        private void DrawSetupMissingParameters(SetupIssue issue, string idPrefix = "SetupMissingParameter_")
        {
            if (issue.missingParameters == null) return;
            var controller = issue.asset as AnimatorController;
            for (int i = 0; i < issue.missingParameters.Count; i++)
            {
                var row = issue.missingParameters[i];
                string rowId = idPrefix + i;
                EditorGUILayout.BeginHorizontal();
                string label = row.name.Length > 0 ? row.name : "(Unassigned)";
                GUILayout.Label(new GUIContent(label, label + "\n" + row.typeHint), GUILayout.MinWidth(60), GUILayout.ExpandWidth(true));
                if (row.missingFromAnimator)
                {
                    using (new EditorGUI.DisabledScope(controller == null || controller != GetAnimatorController() ||
                        EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode))
                    {
                        int options = (int)row.allowedTypes;
                        if (options != 0 && (options & (options - 1)) != 0)
                        {
                            if (HubDropdownButtonWithId(rowId + "_Type", new GUIContent(
                                (row.type.HasValue ? row.type.Value.ToString() : "Choose type") + " ▾", row.typeHint), GUI.skin.button, GUILayout.Width(100)))
                            {
                                var menu = new HubDropdownMenu(this);
                                foreach (int value in AnimatorParameterTypeValues)
                                {
                                    var type = (AnimatorControllerParameterType)value;
                                    if ((row.allowedTypes & GetSetupParameterTypes(type)) == 0) continue;
                                    menu.AddItem(new GUIContent(type.ToString()), row.type == type,
                                        () => { row.type = type; row.typeChosen = true; Repaint(); });
                                }
                                menu.ShowAsContext();
                            }
                        }
                        else GUILayout.Label(new GUIContent(row.type.HasValue ? row.type.Value.ToString() : "Conflicting uses", row.typeHint), GUILayout.Width(100));
                        using (new EditorGUI.DisabledScope(!row.type.HasValue || row.allowedTypes == SetupParameterTypes.None))
                        {
                            if (GUILayout.Button(new GUIContent("Add", "Restore this parameter directly to the loaded Animation Controller with the displayed type. The Animator window can be closed. Supports Undo."), GUILayout.Width(48)))
                            {
                                var type = row.type.Value;
                                EditorApplication.delayCall += () => { if (this != null) AddSetupMissingParameter(controller, row.name, type); };
                            }
                        }
                    }
                }
                else GUILayout.Label(row.name.Length == 0 ? "Assign a name using Show" :
                    controller == null ? "Load an Animator to add" : "Missing from VRC list", EditorStyles.miniLabel);
                if (HubDropdownButtonWithId(rowId + "_Show", new GUIContent("Show ▾", "Open a reference to this missing parameter."), GUI.skin.button, GUILayout.Width(64)))
                    ShowSetupLocations(new SetupIssue { locations = row.locations });
                EditorGUILayout.EndHorizontal();
            }
        }

        private void AddSetupMissingParameter(AnimatorController controller, string name, AnimatorControllerParameterType type)
        {
            if (controller == null || controller != GetAnimatorController() || string.IsNullOrWhiteSpace(name) ||
                EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            // Recheck the references at click time, so stale rows cannot add duplicates or an incompatible type.
            RefreshSetupScan();
            SetupMissingParameter current = null;
            foreach (var issue in _setupIssues)
                if (issue.missingParameters != null)
                    current = issue.missingParameters.Find(row => row.name == name) ?? current;
            if (current == null || !current.missingFromAnimator || (current.allowedTypes & GetSetupParameterTypes(type)) == 0)
            {
                ShowNotification(new GUIContent("The references changed. Review the refreshed scan."));
                Repaint();
                return;
            }

            var parameters = new List<AnimatorControllerParameter>(controller.parameters);
            if (parameters.Exists(parameter => parameter != null && parameter.name == name)) return;
            var parameterToAdd = new AnimatorControllerParameter { name = name, type = type };
            var vrcParameter = _vrcExpressionParametersAsset != null && _vrcExpressionParametersAsset.parameters != null
                ? Array.Find(_vrcExpressionParametersAsset.parameters, parameter => parameter != null && parameter.name == name) : null;
            double defaultValue = vrcParameter != null
                ? ConvertParameterDefault(vrcParameter.defaultValue, GetAnimatorTypeForVrcType((int)vrcParameter.valueType), type) :
                name == "BlendTreeWeight" && type == AnimatorControllerParameterType.Float ? 1d : 0d;
            if (type == AnimatorControllerParameterType.Bool) parameterToAdd.defaultBool = defaultValue != 0;
            else if (type == AnimatorControllerParameterType.Int) parameterToAdd.defaultInt = (int)defaultValue;
            else parameterToAdd.defaultFloat = (float)defaultValue;

            int insertIndex = parameters.Count;
            if (vrcParameter != null)
            {
                var vrcNames = GetVrcParameterNames(_vrcExpressionParametersAsset);
                insertIndex = 0;
                foreach (var row in BuildParameterOrder(parameters.ToArray(), vrcNames))
                {
                    if (row.animatorIndex >= 0) insertIndex++;
                    else if (vrcNames[row.vrcIndex] == name) break;
                }
            }
            Undo.RegisterCompleteObjectUndo(controller, "Restore Missing Animator Parameter");
            parameters.Insert(insertIndex, parameterToAdd);
            controller.parameters = parameters.ToArray();
            EditorUtility.SetDirty(controller);
            _selectedParamIndex = insertIndex;
            _hasParamCache = false;
            _parameterListControllerVersion = _parameterListVrcVersion = -1;
            _parameterUsageScannedName = null;
            InvalidateLayerSummaries();
            AssetDatabase.SaveAssetIfDirty(controller);
            RefreshSetupScan();
            ShowNotification(new GUIContent("Added '" + name + "' as " + type + "."));
            Repaint();
        }

        private void AddSetupComponentIssue(MessageType severity, string message, IEnumerable<Component> components)
        {
            var targets = new List<Component>();
            foreach (var component in components)
                if (component != null && !targets.Contains(component)) targets.Add(component);
            AddSetupIssue(severity, message, null, avatarComponents: targets.ToArray());
        }

        private void ShowSetupIssue(SetupIssue issue)
        {
            // Retain the objects from the scanned avatar, even if the avatar slot changes later.
            // Shared materials can belong to multiple meshes; select every surviving owner.
            var selection = new List<UnityEngine.Object>();
            int componentCount = 0;
            if (issue.avatarComponents != null)
                foreach (Component component in issue.avatarComponents)
                {
                    if (component == null) continue;
                    // Select the owners so Unity highlights them in the Hierarchy, and reveal
                    // the matching components in their Inspectors. Multiple components may share an owner.
                    if (!selection.Contains(component.gameObject)) selection.Add(component.gameObject);
                    UnityEditorInternal.InternalEditorUtility.SetIsInspectorExpanded(component, true);
                    componentCount++;
                }
            if (issue.avatarObjects != null)
                foreach (GameObject avatarObject in issue.avatarObjects)
                    if (avatarObject != null && !selection.Contains(avatarObject)) selection.Add(avatarObject);

            if (selection.Count > 0)
            {
                Selection.objects = selection.ToArray();
                EditorGUIUtility.PingObject(selection[0]);
                if (componentCount > 0)
                    ShowNotification(new GUIContent("Selected " + componentCount + " affected components on " + selection.Count + " avatar objects."));
                else if (selection.Count > 1)
                    ShowNotification(new GUIContent("Selected " + selection.Count + " affected avatar objects."));
                return;
            }

            if (issue.avatarComponents != null || issue.avatarObjects != null)
            {
                ShowNotification(new GUIContent("The affected avatar objects changed. Scan Setup again to refresh."));
                return;
            }
            // Asset-only findings (parameters, menus, controllers) keep their existing navigation.
            if (issue.asset != null)
            {
                Selection.activeObject = issue.asset;
                EditorGUIUtility.PingObject(issue.asset);
            }
        }

        private static Component[] GetSetupContactTargets(SetupIssue issue, bool localOnly)
        {
            if (issue.avatarComponents == null) return Array.Empty<Component>();
            return Array.FindAll(issue.avatarComponents, component =>
                component != null && component is VRC.Dynamics.ContactBase contact && contact.localOnly == localOnly);
        }

        private void ShowSetupContactOptions(SetupIssue issue)
        {
            var menu = new HubDropdownMenu(this);
            foreach (bool localOnly in new[] { true, false })
            {
                string label = localOnly ? "Local Only Contacts" : "Non-Local Contacts";
                int count = GetSetupContactTargets(issue, localOnly).Length;
                var content = new GUIContent(label + " (" + count + ")");
                if (count == 0)
                {
                    menu.AddDisabledItem(content);
                    continue;
                }
                menu.AddItem(content, false, () =>
                {
                    // Recheck Local Only when selected, using the contacts from the scanned avatar.
                    var targets = GetSetupContactTargets(issue, localOnly);
                    if (targets.Length == 0)
                    {
                        ShowNotification(new GUIContent("No matching contacts remain. Scan Setup again to refresh."));
                        return;
                    }
                    var filteredIssue = issue;
                    filteredIssue.avatarComponents = targets;
                    filteredIssue.avatarObjects = null;
                    filteredIssue.asset = null;
                    ShowSetupIssue(filteredIssue);
                });
            }
            menu.ShowAsContext();
        }

        private void CollapseSetupHierarchy()
        {
            if (_avatarRoot == null || EditorUtility.IsPersistent(_avatarRoot))
            {
                ShowNotification(new GUIContent("Choose an Avatar Root from the Hierarchy first."));
                return;
            }
            // Collapse the avatar branch, then reopen only its root so all child foldouts
            // stay closed. Unity's Hierarchy type is internal.
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var windowType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SceneHierarchyWindow");
            var collapse = windowType != null
                ? windowType.GetMethod("SetExpandedRecursive", flags, null, new[] { typeof(int), typeof(bool) }, null) : null;
            var setExpanded = windowType != null
                ? windowType.GetMethod("SetExpanded", flags, null, new[] { typeof(int), typeof(bool) }, null) : null;
            if (collapse == null || setExpanded == null)
            {
                ShowNotification(new GUIContent("Collapse Hierarchy is unavailable in this Unity version."));
                return;
            }
            int collapsed = 0;
            var arguments = new object[] { _avatarRoot.GetInstanceID(), false };
            var expandRootArguments = new object[] { _avatarRoot.GetInstanceID(), true };
            try
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(windowType))
                {
                    var window = obj as EditorWindow;
                    if (window == null) continue;
                    collapse.Invoke(window, arguments);
                    setExpanded.Invoke(window, expandRootArguments);
                    window.Repaint();
                    collapsed++;
                }
            }
            catch (Exception)
            {
                ShowNotification(new GUIContent("Could not collapse the Avatar Root's children in the Hierarchy."));
                return;
            }
            if (collapsed == 0) ShowNotification(new GUIContent("No open Hierarchy window to collapse."));
        }

        private void TrackSetupAsset(UnityEngine.Object asset)
        {
            if (asset != null) _setupAssetVersions[asset] = EditorUtility.GetDirtyCount(asset);
        }

        private void CheckSetupFreshness()
        {
            if (this == null || !_hasSetupScan || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode || _hubDropdown != null || GUIUtility.hotControl != 0) return;
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextSetupFreshnessCheck) return;
            // Background editor notifications and dirty counts only mark results as stale.
            // Fix actions refresh explicitly after finishing, so notifications cannot cause a rescan loop.
            _nextSetupFreshnessCheck = now + 1d;
            if (_setupTargetAtScan != _targetAsset || _setupParametersAtScan != _vrcExpressionParametersAsset ||
                _setupMenuAtScan != _vrcExpressionMenuAsset || _setupAvatarAtScan != _avatarRoot ||
                _setupMobileAtScan != IsSetupMobilePlatform())
                _setupChecksStale = true;
            if (!_setupChecksStale)
            {
                foreach (var version in _setupAssetVersions)
                {
                    if (version.Key == null || EditorUtility.GetDirtyCount(version.Key) != version.Value)
                    {
                        _setupChecksStale = true;
                        break;
                    }
                }
            }
        }

        private void RefreshSetupScan()
        {
            Vector2 scroll = _setupScrollPos;
            var chosenTypes = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
            if (_setupTargetAtScan == _targetAsset)
                foreach (var issue in _setupIssues)
                    if (issue.missingParameters != null)
                        foreach (var row in issue.missingParameters)
                            if (row.typeChosen && row.type.HasValue)
                                chosenTypes[row.name] = row.type.Value;
            RunSetupScan();
            foreach (var issue in _setupIssues)
                if (issue.missingParameters != null)
                    foreach (var row in issue.missingParameters)
                        if (chosenTypes.TryGetValue(row.name, out var type) &&
                            (row.allowedTypes & GetSetupParameterTypes(type)) != 0)
                        {
                            row.type = type;
                            row.typeChosen = true;
                        }
            _setupScrollPos = scroll;
            Repaint();
        }

        private void RunSetupScan()
        {
            _setupAssetVersions.Clear();
            ScanLoadedSetupAssets();
            ScanSetupAvatar();
            _setupIssues.Sort((a, b) => b.severity.CompareTo(a.severity));
            _setupTargetAtScan = _targetAsset;
            _setupParametersAtScan = _vrcExpressionParametersAsset;
            _setupMenuAtScan = _vrcExpressionMenuAsset;
            _setupAvatarAtScan = _avatarRoot;
            _setupMobileAtScan = IsSetupMobilePlatform();
            _hasSetupScan = true;
            _setupChecksStale = false;
            _setupScrollPos = Vector2.zero;
            _nextSetupFreshnessCheck = EditorApplication.timeSinceStartup + 1;
        }

        private void CollectSetupWriteDefaultsStates(AnimatorController controller, HashSet<AnimatorState> states,
            HashSet<AnimatorState> directStates, bool trackAssets = false)
        {
            if (controller == null) return;
            var layers = controller.layers;
            var visitedMachines = new HashSet<AnimatorStateMachine>();
            var visitedTrees = new HashSet<BlendTree>();

            bool ContainsDirectBlendTree(Motion motion)
            {
                if (!(motion is BlendTree tree) || !visitedTrees.Add(tree)) return false;
                if (trackAssets) TrackSetupAsset(tree);
                if (tree.blendType == BlendTreeType.Direct) return true;
                foreach (var child in tree.children)
                    if (ContainsDirectBlendTree(child.motion)) return true;
                return false;
            }

            void CollectStates(AnimatorStateMachine machine, int layerIndex)
            {
                if (machine == null || !visitedMachines.Add(machine)) return;
                if (trackAssets) TrackSetupAsset(machine);
                foreach (var child in machine.states)
                {
                    var state = child.state;
                    if (state == null) continue;
                    states.Add(state);
                    if (trackAssets) TrackSetupAsset(state);
                    if (directStates.Contains(state)) continue;
                    visitedTrees.Clear();
                    // Synced layers share Write Defaults with their source state, so protect
                    // that state if either its own motion or a synced override uses a Direct tree.
                    if (ContainsDirectBlendTree(state.motion) ||
                        (layers[layerIndex].syncedLayerIndex >= 0 &&
                         ContainsDirectBlendTree(controller.GetStateEffectiveMotion(state, layerIndex))))
                        directStates.Add(state);
                }
                foreach (var child in machine.stateMachines)
                    CollectStates(child.stateMachine, layerIndex);
            }

            for (int i = 0; i < layers.Length; i++)
            {
                visitedMachines.Clear();
                CollectStates(GetParameterUsageLayerRoot(controller, i), i);
            }
        }

        private void SetSetupWriteDefaults(AnimatorController controller, bool enabled)
        {
            if (controller == null || controller != GetAnimatorController()) return;
            var states = new HashSet<AnimatorState>();
            var directStates = new HashSet<AnimatorState>();
            CollectSetupWriteDefaultsStates(controller, states, directStates);
            if (states.Count == 0)
            {
                RefreshSetupScan();
                ShowNotification(new GUIContent("No states in the loaded FX controller."));
                return;
            }

            var changedStates = new List<UnityEngine.Object>();
            foreach (var state in states)
                if (state.writeDefaultValues != (enabled || directStates.Contains(state)))
                    changedStates.Add(state);

            if (changedStates.Count > 0)
            {
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                string undoName = enabled ? "Turn FX Write Defaults On" : "Turn FX Write Defaults Off";
                Undo.SetCurrentGroupName(undoName);
                Undo.RecordObjects(changedStates.ToArray(), undoName);
                foreach (AnimatorState state in changedStates)
                {
                    state.writeDefaultValues = enabled || directStates.Contains(state);
                    EditorUtility.SetDirty(state);
                }
                EditorUtility.SetDirty(controller);
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(undoGroup);
                InvalidateLayerSummaries();
                if (_hasSetupScan) _setupChecksStale = true;
            }

            string message = changedStates.Count > 0
                ? "Write Defaults updated on " + changedStates.Count + " states."
                : "Write Defaults already match.";
            GUI.FocusControl(null);
            RefreshSetupScan();
            ShowNotification(new GUIContent(message));
            Repaint();
        }

        private void DrawSetupWriteDefaultsOptions(AnimatorController controller, string dropdownId)
        {
            using (new EditorGUI.DisabledScope(controller == null || controller != GetAnimatorController()))
            {
                if (!HubDropdownButtonWithId(dropdownId, new GUIContent("Write Defaults ▾", "Set Write Defaults across the loaded FX controller. Direct BlendTree states stay on."), GUI.skin.button, GUILayout.Width(130))) return;
                var menu = new HubDropdownMenu(this);
                menu.AddItem(new GUIContent("Write Defaults On", "Turn Write Defaults on for every state in every layer of the loaded FX controller. Supports Undo."), false,
                    () => SetSetupWriteDefaults(controller, true),
                    enabledNow: () => controller != null && controller == GetAnimatorController());
                menu.AddItem(new GUIContent("Write Defaults Off", "Turn Write Defaults off for every state except those using Direct BlendTrees, including nested trees and synced layer motions. Supports Undo."), false,
                    () => SetSetupWriteDefaults(controller, false),
                    enabledNow: () => controller != null && controller == GetAnimatorController());
                menu.ShowAsContext();
            }
        }

        private void DrawSetupChecks()
        {
            if (!_showSetupChecks)
            {
                FinishSetupListResize();
                return;
            }

            EditorGUILayout.LabelField("Avatar Setup", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            EditorGUI.BeginChangeCheck();
            _setupPlatform = (SetupPlatform)HubPopup("SetupPlatform", "Check Platform", (int)_setupPlatform, SetupPlatformNames);
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetInt(GetProjectScopedPreferenceKey(PrefSetupPlatformKey), (int)_setupPlatform);
                if (_hasSetupScan) _setupChecksStale = true;
            }

            if (_targetAsset is AnimatorOverrideController)
            {
                EditorGUILayout.HelpBox("An Animator Override Controller is loaded. This editor needs a regular Animator Controller to edit layers and Blend Trees. Menu and VRC parameter checks are still available.", MessageType.Info);
            }
            EditorGUILayout.LabelField("Checks the avatar and loaded slots for removal limits, unsupported components and setup problems. Build tools can change the final avatar.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan Setup", GUILayout.Width(110))) RunSetupScan();
            using (new EditorGUI.DisabledScope(_avatarRoot == null || EditorUtility.IsPersistent(_avatarRoot)))
            {
                if (GUILayout.Button(new GUIContent("Collapse Hierarchy", "Collapse the Avatar Root's children while keeping the root open."), GUILayout.Width(140)))
                    CollapseSetupHierarchy();
            }
            AnimatorController setupController = GetAnimatorController();
            if (_hasSetupScan)
            {
                int errors = 0, warnings = 0;
                foreach (var issue in _setupIssues)
                {
                    if (issue.severity == MessageType.Error) errors++;
                    if (issue.severity == MessageType.Warning) warnings++;
                }
                GUILayout.Label(errors + " errors, " + warnings + " warnings", EditorStyles.miniLabel);
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(IsSetupMobilePlatform() ? "Mobile" : "PC", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();

            using var setupScrollView = new NestedScrollViewScope(ref _setupScrollPos, GUILayout.Height(_setupListHeight));
            if (_hasSetupScan)
            {
                CheckSetupFreshness();
                if (_setupChecksStale)
                {
                    EditorGUILayout.HelpBox("The avatar, assets or platform changed since the last scan. Click Scan Setup to refresh these results.", MessageType.Info);
                }
                else if (_setupIssues.Count == 0)
                {
                    EditorGUILayout.HelpBox("No issues found by these checks in the selected avatar and loaded assets.", MessageType.Info);
                }

                if (_setupIssues.Count > 0)
                {
                    var messageStyle = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { alignment = TextAnchor.MiddleLeft };
                    foreach (var issue in _setupIssues)
                    {
                        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                        EditorGUILayout.BeginHorizontal();
                        string iconName = issue.severity == MessageType.Error ? "console.erroricon" :
                            issue.severity == MessageType.Warning ? "console.warnicon" : "console.infoicon";
                        if (issue.severity != MessageType.None)
                            GUILayout.Label(EditorGUIUtility.IconContent(iconName), GUIStyle.none, GUILayout.Width(32), GUILayout.Height(32));
                        EditorGUILayout.BeginVertical(GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
                        GUILayout.Label(issue.message, messageStyle, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
                        EditorGUILayout.BeginHorizontal();
                        if (issue.missingParameters != null && issue.missingParameters.Count > 0)
                        {
                            bool expanded = GUILayout.Toggle(_showSetupMissingParameters,
                                new GUIContent("Parameters " + (_showSetupMissingParameters ? "▾" : "▸"),
                                    "Expand or collapse the missing parameters and their Add controls."), "Button", GUILayout.Width(105));
                            if (expanded != _showSetupMissingParameters)
                            {
                                _showSetupMissingParameters = expanded;
                                CloseHubDropdown();
                            }
                        }
                        if (issue.autoFix != null)
                        {
                            using (new EditorGUI.DisabledScope(issue.asset == null || _setupTargetAtScan != _targetAsset))
                                if (GUILayout.Button(new GUIContent("Auto Fix", "Set all affected layer weights to 1. Supports Undo."), GUILayout.Width(65)))
                                    EditorApplication.delayCall += () => { if (this != null) issue.autoFix(); };
                        }
                        bool hasAvatarObjects = issue.avatarObjects != null && Array.Exists(issue.avatarObjects, target => target != null);
                        bool hasAvatarComponents = issue.avatarComponents != null && Array.Exists(issue.avatarComponents, target => target != null);
                        bool hasContacts = hasAvatarComponents && Array.Exists(issue.avatarComponents,
                            target => target != null && target is VRC.Dynamics.ContactBase);
                        bool hasTargets = issue.avatarObjects != null || issue.avatarComponents != null;
                        bool hasLocations = issue.locations != null && issue.locations.Count > 0;
                        if (issue.writeDefaultsOptions)
                        {
                            using (new EditorGUI.DisabledScope(_setupTargetAtScan != _targetAsset))
                                DrawSetupWriteDefaultsOptions(setupController, issue.locationId);
                        }
                        else if (issue.missingParameters == null)
                        {
                            using (new EditorGUI.DisabledScope(hasLocations ? !issue.locations.Exists(location => location.target != null) :
                                hasTargets ? !hasAvatarObjects && !hasAvatarComponents : issue.asset == null))
                            {
                                string showTooltip = hasAvatarComponents
                                    ? "Select the avatar objects containing the affected components and expand those components in the Inspector."
                                    : hasAvatarObjects
                                    ? "Select the affected objects on the scanned avatar in the Hierarchy. For a shared material, select every mesh using it."
                                    : "Select the affected asset or component and highlight it in Unity.";
                                if (issue.missingScripts != null)
                                {
                                    if (HubDropdownButtonWithId(issue.locationId, new GUIContent("Scripts ▾", "Choose an object containing a missing script."), GUI.skin.button, GUILayout.Width(76)))
                                        ShowSetupMissingScripts(issue);
                                    using (new EditorGUI.DisabledScope(!CanRemoveSetupMissingScripts(issue)))
                                        if (GUILayout.Button(new GUIContent("Remove All Missing Scripts",
                                            "Remove the missing components listed for this avatar. Supports Undo. Use an avatar in the scene or Prefab Mode."), GUILayout.Width(175)))
                                            EditorApplication.delayCall += () => { if (this != null) RemoveSetupMissingScripts(issue); };
                                }
                                else if (hasLocations)
                                {
                                    if (HubDropdownButtonWithId(issue.locationId, new GUIContent("Show ▾", "Choose an affected item to open its location."), GUI.skin.button, GUILayout.Width(64)))
                                        ShowSetupLocations(issue);
                                }
                                else if (hasContacts)
                                {
                                    if (HubDropdownButtonWithId("SetupContacts_" + issue.GetHashCode(), new GUIContent("Show \u25bc", "Choose contacts with Local Only enabled or disabled."), GUI.skin.button, GUILayout.Width(64)))
                                        ShowSetupContactOptions(issue);
                                }
                                else if (GUILayout.Button(new GUIContent("Show", showTooltip), GUILayout.Width(48)))
                                    ShowSetupIssue(issue);
                            }
                        }
                        if (issue.asset != null && issue.avatarObjects != null && (issue.asset is Material || issue.asset is Shader || issue.asset is Mesh))
                        {
                            if (GUILayout.Button(new GUIContent("Asset", "Select the asset itself in the Inspector and highlight it in the Project window."), GUILayout.Width(48)))
                            {
                                Selection.activeObject = issue.asset;
                                EditorGUIUtility.PingObject(issue.asset);
                            }
                        }
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.EndHorizontal();
                        EditorGUILayout.EndVertical();
                        EditorGUILayout.EndHorizontal();
                        if (_showSetupMissingParameters) DrawSetupMissingParameters(issue);
                        EditorGUILayout.EndVertical();
                    }
                }
            }
            else EditorGUILayout.HelpBox("Click Scan Setup to check the avatar and loaded assets.", MessageType.Info);
            setupScrollView.Dispose();
            DrawSetupListResizer();
            EditorGUILayout.EndVertical();
            GUILayout.Space(5);
        }

        private void DrawSetupListResizer()
        {
            Event e = Event.current;
            Rect rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(6), GUILayout.ExpandWidth(true));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);
            if (e.type == EventType.Repaint) EditorGUI.DrawRect(new Rect(rect.x, rect.y + 2, rect.width, 2), new Color(0.15f, 0.15f, 0.15f));
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                _isResizingSetupList = true;
                e.Use();
            }
            if (_isResizingSetupList && e.type == EventType.MouseDrag)
            {
                _setupListHeight = Mathf.Clamp(_setupListHeight + e.delta.y, 60f, 1000f);
                Repaint();
                e.Use();
            }
            if (_isResizingSetupList && e.rawType == EventType.MouseUp)
            {
                FinishSetupListResize();
                e.Use();
            }
        }

        private void FinishSetupListResize()
        {
            if (!_isResizingSetupList) return;
            _isResizingSetupList = false;
            EditorPrefs.SetFloat(PrefSetupListHeightKey, _setupListHeight);
        }

        private void ScanLoadedSetupAssets()
        {
            _setupIssues.Clear();
            AnimatorController controller = GetAnimatorController();
            TrackSetupAsset(controller);
            TrackSetupAsset(_vrcExpressionParametersAsset);
            var missingMotions = new List<ParameterUsageEntry>();
            var missingParameters = new List<ParameterUsageEntry>();
            var animatorParameters = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
            if (controller != null)
            {
                foreach (AnimatorControllerParameter parameter in controller.parameters)
                {
                    if (parameter == null || string.IsNullOrEmpty(parameter.name)) continue;
                    if (animatorParameters.ContainsKey(parameter.name))
                        AddSetupIssue(MessageType.Warning, "Animator parameter '" + parameter.name + "' is listed more than once.", controller);
                    else
                        animatorParameters.Add(parameter.name, parameter.type);
                }
            }

            var expressionParameters = new Dictionary<string, VRCExpressionParameters.Parameter>(StringComparer.Ordinal);
            if (_vrcExpressionParametersAsset != null)
            {
                var parameters = _vrcExpressionParametersAsset.parameters;
                bool canCalculateCost = parameters != null;
                if (parameters == null)
                    AddSetupIssue(MessageType.Warning, "The loaded VRC Parameters asset has no parameter list.", _vrcExpressionParametersAsset);
                else
                {
                    foreach (var parameter in parameters)
                    {
                        if (parameter == null)
                        {
                            canCalculateCost = false;
                            AddSetupIssue(MessageType.Warning, "The loaded VRC Parameters asset contains an empty entry.", _vrcExpressionParametersAsset);
                            continue;
                        }
                        if (string.IsNullOrEmpty(parameter.name)) continue;
                        if (expressionParameters.ContainsKey(parameter.name))
                            AddSetupIssue(MessageType.Warning, "VRC parameter '" + parameter.name + "' is listed more than once.", _vrcExpressionParametersAsset);
                        else
                            expressionParameters.Add(parameter.name, parameter);

                        if (VrcBuiltInParameterNames.Contains(parameter.name))
                            AddSetupIssue(MessageType.Warning, "'" + parameter.name + "' is supplied by VRChat and should not be listed in VRC Parameters.", _vrcExpressionParametersAsset);
                    }
                }
                if (canCalculateCost)
                {
                    int cost = GetSetupSyncedParameterCost(_vrcExpressionParametersAsset);
                    int limit = SetupSyncedParameterBitLimit;
                    if (cost > limit)
                        AddSetupIssue(MessageType.Error, "The loaded VRC Parameters asset uses " + cost + " synced bits; the SDK limit is " + limit + ".", _vrcExpressionParametersAsset);
                }
            }

            if (_vrcExpressionMenuAsset != null)
            {
                if (_vrcExpressionParametersAsset == null)
                    AddSetupIssue(MessageType.Info, "Load the matching VRC Parameters asset to check whether this menu's parameters are declared.", _vrcExpressionMenuAsset);
                ScanSetupMenu(_vrcExpressionMenuAsset, expressionParameters, new HashSet<VRCExpressionsMenu>(),
                    new List<VRCExpressionsMenu>(), missingParameters);
            }
            else if (_vrcExpressionParametersAsset != null)
                AddSetupIssue(MessageType.Info, "No VRC Menu Root is loaded. Menu references have not been checked.", _vrcExpressionParametersAsset);

            if (controller != null)
            {
                ScanSetupAnimatorReferences(controller, animatorParameters, expressionParameters, missingMotions, missingParameters);
                ScanSetupWriteDefaults(controller);
                ScanSetupLayerWeights(controller);
            }
            if (missingMotions.Count > 0)
                AddSetupIssue(MessageType.Warning, "Missing motions: " + missingMotions.Count +
                    " broken motion references. Choose an item in Show to open its location.", controller,
                    locations: missingMotions, locationId: "SetupMissingMotions");
            if (missingParameters.Count > 0)
            {
                var parameterRows = BuildSetupMissingParameterRows(controller, animatorParameters, expressionParameters, missingParameters);
                AddSetupIssue(MessageType.Warning, "Missing parameters: " + missingParameters.Count +
                    " missing or unassigned references in the loaded assets. Open Parameters to review, add, or show a reference.", controller,
                    locations: missingParameters, locationId: "SetupMissingParameters",
                    missingParameters: parameterRows);
            }
        }

        private Motion FindSetupLayerWeightContent(AnimatorController controller, int layerIndex,
            Dictionary<AnimationClip, bool> clipContents)
        {
            var root = GetParameterUsageLayerRoot(controller, layerIndex);
            if (root == null) return null;
            var layers = controller.layers;
            var visitedMachines = new HashSet<AnimatorStateMachine>();

            bool HasAnimatedProperties(AnimationClip clip)
            {
                TrackSetupAsset(clip);
                if (clipContents.TryGetValue(clip, out bool hasContent)) return hasContent;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve != null && curve.length > 0) { hasContent = true; break; }
                }
                if (!hasContent)
                    foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                    {
                        var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                        if (keys != null && keys.Length > 0) { hasContent = true; break; }
                    }
                clipContents[clip] = hasContent;
                return hasContent;
            }

            Motion FindContent(AnimatorStateMachine machine)
            {
                if (machine == null || !visitedMachines.Add(machine)) return null;
                TrackSetupAsset(machine);
                foreach (var child in machine.states)
                {
                    var state = child.state;
                    if (state == null) continue;
                    TrackSetupAsset(state);
                    Motion motion = null;
                    // The root lookup above validates the sync chain. Follow overrides back
                    // to the source so an empty replacement clip stays empty for this layer.
                    for (int index = layerIndex; index >= 0 && index < layers.Length; index = layers[index].syncedLayerIndex)
                    {
                        motion = layers[index].syncedLayerIndex < 0 ? state.motion : layers[index].GetOverrideMotion(state);
                        if (motion != null) break;
                    }
                    if (motion is BlendTree)
                    {
                        TrackSetupAsset(motion);
                        return motion;
                    }
                    if (motion is AnimationClip clip && HasAnimatedProperties(clip)) return clip;
                }
                foreach (var child in machine.stateMachines)
                {
                    Motion content = FindContent(child.stateMachine);
                    if (content != null) return content;
                }
                return null;
            }

            return FindContent(root);
        }

        private void ScanSetupLayerWeights(AnimatorController controller)
        {
            var layers = controller.layers;
            var clipContents = new Dictionary<AnimationClip, bool>();
            var affectedLayers = new List<string>();
            // Unity's base layer always has effective weight 1, regardless of the stored default.
            for (int i = 1; i < layers.Length; i++)
            {
                var layer = layers[i];
                if (layer == null || layer.defaultWeight != 0f) continue;
                Motion content = FindSetupLayerWeightContent(controller, i, clipContents);
                if (content == null) continue;
                string reason = content is BlendTree
                    ? "BlendTree '" + content.name + "'"
                    : "animation '" + content.name + "' with animated properties";
                affectedLayers.Add("'" + layer.name + "': " + reason);
            }
            if (affectedLayers.Count == 0) return;
            string summary = affectedLayers.Count == 1
                ? "1 layer has weight 0. Set its weight to 1."
                : affectedLayers.Count + " layers have weight 0. Set their weights to 1.";
            AddSetupIssue(MessageType.Warning, summary + "\n" + string.Join("\n", affectedLayers), controller,
                autoFix: () => FixSetupLayerWeights(controller));
        }

        private void FixSetupLayerWeights(AnimatorController controller)
        {
            if (controller == null || controller != GetAnimatorController()) return;
            var layers = controller.layers;
            var affectedLayers = new List<int>();
            var clipContents = new Dictionary<AnimationClip, bool>();
            // Recheck the current layers so a changed or reordered controller is handled correctly.
            for (int i = 1; i < layers.Length; i++)
            {
                if (layers[i] == null || layers[i].defaultWeight != 0f) continue;
                if (FindSetupLayerWeightContent(controller, i, clipContents) != null) affectedLayers.Add(i);
            }
            if (affectedLayers.Count > 0)
            {
                Undo.RegisterCompleteObjectUndo(controller, "Set Layer Weights to 1");
                foreach (int index in affectedLayers) layers[index].defaultWeight = 1f;
                controller.layers = layers;
                EditorUtility.SetDirty(controller);
                InvalidateLayerSummaries();
                ShowNotification(new GUIContent("Set " + affectedLayers.Count +
                    (affectedLayers.Count == 1 ? " layer's weight to 1." : " layer weights to 1.")));
            }
            RefreshSetupScan();
            Repaint();
        }

        private void ScanSetupWriteDefaults(AnimatorController controller)
        {
            var states = new HashSet<AnimatorState>();
            var directStates = new HashSet<AnimatorState>();
            CollectSetupWriteDefaultsStates(controller, states, directStates, trackAssets: true);
            int onCount = 0, offCount = 0;
            foreach (var state in states)
            {
                if (directStates.Contains(state))
                {
                    if (!state.writeDefaultValues)
                        AddSetupIssue(MessageType.Warning,
                            "State '" + state.name + "' uses a Direct BlendTree but has Write Defaults Off. " +
                            "Use either Write Defaults option below to turn it on.", state,
                            locationId: "SetupWriteDefaultsState_" + state.GetInstanceID(), writeDefaultsOptions: true);
                    // Direct tree states intentionally stay on when the rest of the FX uses WD off.
                    continue;
                }
                if (state.writeDefaultValues) onCount++;
                else offCount++;
            }
            if (onCount > 0 && offCount > 0)
                AddSetupIssue(MessageType.Warning,
                    "Mixed Write Defaults in the loaded FX controller: " + onCount + " states are On and " + offCount +
                    " are Off (excluding states using Direct BlendTrees). Use Write Defaults below to make them consistent.", controller,
                    locationId: "SetupWriteDefaultsMixed", writeDefaultsOptions: true);
        }

        private bool IsSetupMobilePlatform()
        {
            if (_setupPlatform == SetupPlatform.Mobile) return true;
            if (_setupPlatform == SetupPlatform.PC) return false;
            return EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android ||
                EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS;
        }

        private void ScanSetupAvatar()
        {
            if (_avatarRoot == null)
            {
                AddSetupIssue(MessageType.Info, "Assign Avatar Root in the top slots, or click Use Selected Avatar, to check component removal limits. Only the loaded asset slots were checked.", null);
                return;
            }

            bool mobile = IsSetupMobilePlatform();
            GameObject root = _avatarRoot;
            try
            {
                // Read authored component data only. Do not initialize dynamics, refresh SDK managers,
                // invoke SDK validation, or read its runtime bone/constraint caches.
                var components = new List<Component>();
                var physBones = new List<VRC.Dynamics.VRCPhysBoneBase>();
                var colliders = new HashSet<VRC.Dynamics.VRCPhysBoneColliderBase>();
                var contacts = new List<VRC.Dynamics.ContactBase>();
                var constraints = new List<VRC.Dynamics.VRCConstraintBase>();
                var unityConstraints = new List<Component>();
                var raycasts = new List<Component>();
                var globalColliders = new List<Component>();
                int localContacts = 0;
                foreach (Component component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || IsSetupEditorOnly(component)) continue;
                    components.Add(component);
                    if (component is VRC.Dynamics.VRCPhysBoneBase physBone) physBones.Add(physBone);
                    if (component is VRC.Dynamics.VRCPhysBoneColliderBase collider)
                    {
                        colliders.Add(collider);
                        if ((int)collider.globalCollision != 0) globalColliders.Add(collider);
                    }
                    if (component is VRC.Dynamics.ContactBase contact)
                    {
                        contacts.Add(contact);
                        if (contact.localOnly) localContacts++;
                    }
                    if (component is VRC.Dynamics.VRCConstraintBase constraint) constraints.Add(constraint);
                    if (component is UnityEngine.Animations.IConstraint) unityConstraints.Add(component);
                    // A name check keeps this optional component compatible with older SDK versions.
                    if (component.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCRaycast") raycasts.Add(component);
                }

                var allConstraints = new List<Component>(constraints);
                allConstraints.AddRange(unityConstraints);
                if (mobile)
                {
                    ScanSetupMobileDynamics(root, physBones, colliders, contacts, contacts.Count - localContacts, constraints, unityConstraints);
                    // Local Only contacts avoid the mobile sync limit, but still consume component capacity.
                    if (contacts.Count - localContacts <= SetupMobileContactLimit)
                        AddSetupComponentCap("Contacts (including Local Only)", contacts.Count, 256, contacts);
                    AddSetupUnsupportedComponents<Light>("Lights", components);
                    AddSetupUnsupportedComponents<Cloth>("Cloth components", components);
                    AddSetupUnsupportedComponents<Camera>("Cameras", components);
                    AddSetupUnsupportedComponents<Collider>("Unity physics colliders", components);
                    AddSetupUnsupportedComponents<Rigidbody>("Unity rigidbodies", components);
                    AddSetupUnsupportedComponents<AudioSource>("Audio sources", components);
                    ScanSetupMobileShaders(components);
                }
                else
                {
                    // Absolute component caps, not the PC performance rank recommendations.
                    AddSetupComponentCap("PhysBone components", physBones.Count, 256, physBones);
                    AddSetupComponentCap("PhysBone colliders", colliders.Count, 256, colliders);
                    AddSetupComponentCap("Contacts (including Local Only)", contacts.Count, 256, contacts);
                    AddSetupComponentCap("Constraint components", allConstraints.Count, 2000, allConstraints);
                }
                AddSetupComponentCap("Global PhysBone colliders", globalColliders.Count, 4, globalColliders);
                AddSetupComponentCap("VRChat raycasts", raycasts.Count, 80, raycasts);
            }
            catch (Exception exception)
            {
                AddSetupIssue(MessageType.Warning, "The component scan could not finish: " + exception.Message +
                    ". Fix the affected component and scan again; these results are incomplete.", root);
            }
            // Keep freshness tracking and missing-script checks even if a component is malformed.
            TrackSetupAvatarHierarchy(root);
        }

        private void ScanSetupMobileDynamics(GameObject root, List<VRC.Dynamics.VRCPhysBoneBase> physBones,
            HashSet<VRC.Dynamics.VRCPhysBoneColliderBase> colliders, List<VRC.Dynamics.ContactBase> contacts, int nonLocalContactCount,
            List<VRC.Dynamics.VRCConstraintBase> constraints, List<Component> unityConstraints)
        {
            var physBoneRoots = new HashSet<Transform>();
            foreach (var physBone in physBones)
                physBoneRoots.Add(physBone.rootTransform != null ? physBone.rootTransform : physBone.transform);

            var usedColliders = new HashSet<VRC.Dynamics.VRCPhysBoneColliderBase>();
            foreach (var collider in colliders)
                if ((int)collider.globalCollision != 0) usedColliders.Add(collider);

            long affectedTransforms = 0, collisionChecks = 0;
            var pending = new Stack<Transform>();
            foreach (var physBone in physBones)
            {
                Transform boneRoot = physBone.rootTransform != null ? physBone.rootTransform : physBone.transform;
                if (!boneRoot.IsChildOf(root.transform) || IsSetupEditorOnly(boneRoot))
                {
                    AddSetupIssue(MessageType.Warning, "PhysBone '" + physBone.name +
                        "' targets a root outside the included avatar hierarchy. Its transforms cannot be counted reliably; assign an avatar transform.", physBone);
                    continue;
                }

                int attachedColliders = 0;
                if (physBone.colliders != null)
                    foreach (var collider in physBone.colliders)
                    {
                        if (collider == null || !colliders.Contains(collider)) continue;
                        usedColliders.Add(collider);
                        attachedColliders++; // Repeated list entries still add collision work.
                    }

                var ignored = physBone.ignoreTransforms != null
                    ? new HashSet<Transform>(physBone.ignoreTransforms) : new HashSet<Transform>();
                bool hasEndpoint = physBone.endpointPosition != Vector3.zero;
                pending.Push(boneRoot);
                while (pending.Count > 0)
                {
                    Transform transform = pending.Pop();
                    affectedTransforms++; // Count actual transforms, including the root, once per PhysBone.
                    int children = 0;
                    for (int i = 0; i < transform.childCount; i++)
                    {
                        Transform child = transform.GetChild(i);
                        if (ignored.Contains(child) || IsSetupEditorOnly(child) ||
                            (physBone.ignoreOtherPhysBones && physBoneRoots.Contains(child))) continue;
                        children++;
                        pending.Push(child);
                    }
                    // Endpoints add a colliding segment, not another affected Unity Transform.
                    if (children == 1 || (children > 1 && (int)physBone.multiChildType != 0) ||
                        (children == 0 && hasEndpoint))
                        collisionChecks += attachedColliders;
                }
            }

            // Keep outcomes specific to the exceeded category, matching the SDK 3.10.5
            // performance messages. Counts are still calculated here without running SDK validation.
            AddSetupMobileRemovalLimit("PhysBone components", physBones.Count, SetupMobilePhysBoneLimit, "PhysBone components", physBones);
            AddSetupMobileRemovalLimit("PhysBone affected transforms", affectedTransforms, SetupMobileTransformLimit, "PhysBone components", physBones);
            AddSetupMobileRemovalLimit("Used PhysBone colliders", usedColliders.Count, SetupMobileColliderLimit, "PhysBone collider components", colliders);
            AddSetupMobileRemovalLimit("PhysBone collision checks", collisionChecks, SetupMobileCollisionCheckLimit, "PhysBone collider components", colliders);
            AddSetupMobileRemovalLimit("Contacts (excluding Local Only)", nonLocalContactCount, SetupMobileContactLimit, "Contact components", contacts);
            var allConstraints = new List<Component>(constraints);
            allConstraints.AddRange(unityConstraints);
            int constraintCount = allConstraints.Count;
            AddSetupMobileRemovalLimit("Constraint components", constraintCount, SetupMobileConstraintLimit, "constraint components", allConstraints);

            // Depth cannot exceed count. Skip the dependency graph when count already proves removal.
            if (constraintCount > SetupMobileConstraintDepthLimit && constraintCount <= SetupMobileConstraintLimit)
            {
                bool hasCycle;
                int depth = GetSetupConstraintDepth(constraints, out hasCycle);
                if (hasCycle)
                    AddSetupComponentIssue(MessageType.Warning, "Mobile — Constraints contain a dependency loop. Fix the loop before relying on the constraint depth check.", constraints);
                if (!hasCycle)
                {
                    // Unity constraints are serialized for the pre-build depth statistic. Converting them
                    // to VRChat constraints can reduce that estimate without changing their count.
                    depth += unityConstraints.Count;
                    if (unityConstraints.Count > 0 && depth > SetupMobileConstraintDepthLimit)
                        AddSetupComponentIssue(MessageType.Warning, "Mobile — Estimated constraint depth: " + depth + " / " +
                            SetupMobileConstraintDepthLimit + ". Unity constraints are counted sequentially. Convert them to VRChat constraints and scan again. If the final depth is over this limit, VRChat removes the avatar's VRChat constraint components at runtime.", allConstraints);
                    else
                        AddSetupMobileRemovalLimit("Constraint dependency depth", depth, SetupMobileConstraintDepthLimit, "VRChat constraint components", constraints);
                }
            }
        }

        private static int GetSetupConstraintDepth(List<VRC.Dynamics.VRCConstraintBase> constraints, out bool hasCycle)
        {
            int count = constraints.Count;
            var targets = new Transform[count];
            var sources = new List<Transform>[count];
            var next = new List<int>[count];
            var incoming = new int[count];
            var depths = new int[count];
            for (int i = 0; i < count; i++)
            {
                var constraint = constraints[i];
                targets[i] = constraint.TargetTransform != null ? constraint.TargetTransform : constraint.transform;
                sources[i] = new List<Transform>();
                next[i] = new List<int>();
                for (int j = 0; j < constraint.Sources.Count; j++)
                {
                    Transform source = constraint.Sources[j].SourceTransform;
                    if (source != null) sources[i].Add(source);
                }
                // Up objects also create dependencies for aim/look-at constraints.
                var aim = constraint as VRC.Dynamics.ManagedTypes.VRCAimConstraintBase;
                var lookAt = constraint as VRC.Dynamics.ManagedTypes.VRCLookAtConstraintBase;
                if (aim != null && ((int)aim.WorldUp == 1 || (int)aim.WorldUp == 2) && aim.WorldUpTransform != null)
                    sources[i].Add(aim.WorldUpTransform);
                if (lookAt != null && lookAt.UseUpTransform && lookAt.WorldUpTransform != null)
                    sources[i].Add(lookAt.WorldUpTransform);
            }
            for (int i = 0; i < count; i++)
                for (int j = i + 1; j < count; j++)
                {
                    int before = -1, after = -1;
                    if (targets[i] == targets[j])
                    {
                        // Components on one object use their Inspector order (the collection order).
                        if (constraints[i].gameObject == constraints[j].gameObject) { before = i; after = j; }
                    }
                    else if (targets[j].IsChildOf(targets[i])) { before = i; after = j; }
                    else if (targets[i].IsChildOf(targets[j])) { before = j; after = i; }
                    else if (SetupSourcesDependOn(sources[j], targets[i])) { before = i; after = j; }
                    else if (SetupSourcesDependOn(sources[i], targets[j])) { before = j; after = i; }
                    if (before < 0) continue;
                    next[before].Add(after);
                    incoming[after]++;
                }

            // Iterative longest path: no recursive traversal or SDK execution groups are required.
            var ready = new Queue<int>();
            for (int i = 0; i < count; i++)
            {
                depths[i] = 1;
                if (incoming[i] == 0) ready.Enqueue(i);
            }
            int processed = 0, maximum = 0;
            while (ready.Count > 0)
            {
                int current = ready.Dequeue();
                processed++;
                maximum = Mathf.Max(maximum, depths[current]);
                foreach (int dependent in next[current])
                {
                    depths[dependent] = Mathf.Max(depths[dependent], depths[current] + 1);
                    if (--incoming[dependent] == 0) ready.Enqueue(dependent);
                }
            }
            hasCycle = processed != count;
            return maximum;
        }

        private static bool SetupSourcesDependOn(List<Transform> sources, Transform target)
        {
            foreach (Transform source in sources)
                if (source.IsChildOf(target)) return true;
            return false;
        }

        private void AddSetupMobileRemovalLimit(string label, long count, int limit, string removedType, IEnumerable<Component> components)
        {
            if (count <= limit) return;
            AddSetupComponentIssue(MessageType.Error, "Mobile — " + label + ": " + count + " / " + limit +
                " (" + (count - limit) + " over). VRChat will remove the avatar's " + removedType + " at runtime.", components);
        }

        private void AddSetupComponentCap(string label, int count, int limit, IEnumerable<Component> components)
        {
            if (count <= limit) return;
            AddSetupComponentIssue(MessageType.Error, label + ": " + count + " / " + limit +
                " — exceeds the per-avatar component cap. Reduce this count before upload.", components);
        }

        private void ScanSetupMobileShaders(List<Component> components)
        {
            var materialUsers = new Dictionary<Material, List<GameObject>>();
            foreach (Component component in components)
            {
                var renderer = component as Renderer;
                if (renderer == null) continue;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    Shader shader = material.shader;
                    if (shader != null && SetupMobileAvatarShaders.Contains(shader.name)) continue;
                    if (!materialUsers.TryGetValue(material, out var users))
                    {
                        users = new List<GameObject>();
                        materialUsers.Add(material, users);
                    }
                    if (!users.Contains(renderer.gameObject)) users.Add(renderer.gameObject);
                }
            }
            foreach (var entry in materialUsers)
            {
                Material material = entry.Key;
                Shader shader = material.shader;
                AddSetupIssue(MessageType.Error, "Mobile — Material '" + material.name + "' uses " +
                    (shader == null ? "a missing shader" : "unsupported shader '" + shader.name + "'") +
                    ". Use a supported VRChat/Mobile avatar shader before upload.", material, entry.Value.ToArray());
            }
        }

        private static bool IsSetupEditorOnly(Component component)
        {
            for (Transform current = component.transform; current != null; current = current.parent)
                if (current.CompareTag("EditorOnly")) return true;
            return false;
        }

        private void AddSetupUnsupportedComponents<T>(string label, List<Component> components) where T : Component
        {
            var affected = new List<Component>();
            foreach (Component candidate in components)
            {
                T component = candidate as T;
                if (component == null) continue;
                affected.Add(component);
            }
            if (affected.Count > 0)
                AddSetupComponentIssue(MessageType.Warning, "Mobile — " + label + ": " + affected.Count +
                    ". These are not supported on mobile avatars and will be disabled or removed in VRChat.", affected);
        }

        private void TrackSetupAvatarHierarchy(GameObject root)
        {
            var missingScripts = new List<SetupMissingScript>();
            var missingScriptCache = new Dictionary<MonoScript, bool>();
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                TrackSetupAsset(transform.gameObject);
                var components = transform.GetComponents<Component>();
                int[] componentIds = null;
                for (int index = 0; index < components.Length; index++)
                {
                    var component = components[index];
                    if (IsSetupMissingScript(component, missingScriptCache))
                    {
                        // Include inactive and EditorOnly objects: missing editor tools also need restoring.
                        if (componentIds == null) componentIds = GetSetupComponentIds(transform.gameObject);
                        string path = AnimationUtility.CalculateTransformPath(transform, root.transform);
                        missingScripts.Add(new SetupMissingScript { owner = transform.gameObject, componentIndex = index,
                            objectPath = root.name + (path.Length > 0 ? "/" + path : ""), componentIds = componentIds,
                            ownerVersion = EditorUtility.GetDirtyCount(transform.gameObject) });
                        TrackSetupAsset(component);
                        continue;
                    }
                    if (IsSetupEditorOnly(component)) continue;
                    TrackSetupAsset(component);
                    var renderer = component as Renderer;
                    if (renderer != null)
                    {
                        foreach (Material material in renderer.sharedMaterials)
                        {
                            if (material == null || _setupAssetVersions.ContainsKey(material)) continue;
                            TrackSetupAsset(material);
                            TrackSetupAsset(material.shader);
                        }
                    }
                }
            }
            if (missingScripts.Count > 0)
            {
                var owners = new List<GameObject>();
                foreach (var entry in missingScripts)
                    if (!owners.Contains(entry.owner)) owners.Add(entry.owner);
                AddSetupIssue(MessageType.Warning, "Missing scripts: " + missingScripts.Count +
                    " component(s) on " + owners.Count + " object(s). Open Scripts to select an affected object.", root, owners.ToArray(),
                    locationId: "SetupMissingScripts", missingScripts: missingScripts);
            }
        }

        private static bool IsSetupMissingScript(Component component, Dictionary<MonoScript, bool> cache)
        {
            if (component == null) return true;
            if (!(component is MonoBehaviour behaviour)) return false;
            var script = MonoScript.FromMonoBehaviour(behaviour);
            if (script == null) return true;
            if (cache.TryGetValue(script, out bool missing)) return missing;
            string path = AssetDatabase.GetAssetPath(script);
            // Deleted packages can leave their old types loaded until Unity successfully recompiles.
            missing = ((path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)) &&
                !System.IO.File.Exists(GetSetupAssetFilePath(path))) || script.GetClass() == null;
            cache.Add(script, missing);
            return missing;
        }

        private static string GetSetupAssetFilePath(string assetPath)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), assetPath);
            if (!System.IO.File.Exists(path) && assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                string prefix = package != null ? "Packages/" + package.name + "/" : null;
                if (prefix != null && assetPath.StartsWith(prefix, StringComparison.Ordinal))
                    path = System.IO.Path.Combine(package.resolvedPath, assetPath.Substring(prefix.Length));
            }
            return path;
        }

        private static int GetSetupSyncedParameterCost(VRCExpressionParameters asset)
        {
            int bits = 0;
            if (asset == null || asset.parameters == null) return bits;
            foreach (var parameter in asset.parameters)
            {
                if (parameter == null || !parameter.networkSynced) continue;
                switch (parameter.valueType)
                {
                    case VRCExpressionParameters.ValueType.Bool: bits++; break;
                    case VRCExpressionParameters.ValueType.Int:
                    case VRCExpressionParameters.ValueType.Float: bits += 8; break;
                }
            }
            return bits;
        }

        private void DrawSyncedParameterMemory()
        {
            if (_vrcExpressionParametersAsset == null) return;
            var parameters = _vrcExpressionParametersAsset.parameters;
            if (parameters == null || Array.Exists(parameters, parameter => parameter == null))
            {
                EditorGUILayout.HelpBox("Parameter memory is unavailable because the VRC parameter list contains missing entries. Run Scan Setup for details.", MessageType.Warning);
                return;
            }
            int used = GetSetupSyncedParameterCost(_vrcExpressionParametersAsset);
            int limit = SetupSyncedParameterBitLimit;
            Rect bar = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(bar, limit > 0 ? Mathf.Clamp01((float)used / limit) : 0,
                "Synced parameter memory: " + used + " / " + limit + " bits" +
                (used > limit ? " - " + (used - limit) + " over budget" : " - " + (limit - used) + " free"));
            GUILayout.Space(3);
        }

        private void ScanSetupMenu(VRCExpressionsMenu menu,
            Dictionary<string, VRCExpressionParameters.Parameter> expressionParameters,
            HashSet<VRCExpressionsMenu> visitedMenus, List<VRCExpressionsMenu> parentPath, List<ParameterUsageEntry> missingParameters)
        {
            if (menu == null || !visitedMenus.Add(menu)) return;
            TrackSetupAsset(menu);
            var menuPath = new List<VRCExpressionsMenu>(parentPath) { menu };
            if (menu.controls == null)
            {
                AddSetupIssue(MessageType.Warning, "Menu '" + menu.name + "' has no controls list.", menu);
                return;
            }
            if (menu.controls.Count > VRCExpressionsMenu.MAX_CONTROLS)
                AddSetupIssue(MessageType.Warning, "Menu '" + menu.name + "' has " + menu.controls.Count + " controls; the SDK limit is " + VRCExpressionsMenu.MAX_CONTROLS + ". Build tools may split this menu during upload.", menu);

            for (int i = 0; i < menu.controls.Count; i++)
            {
                var control = menu.controls[i];
                if (control == null)
                {
                    AddSetupIssue(MessageType.Warning, "Menu '" + menu.name + "' contains an empty control at position " + (i + 1) + ".", menu);
                    continue;
                }
                string controlName = string.IsNullOrEmpty(control.name) ? "Control " + (i + 1) : MenuPlainText(control.name);
                string location = "Menu " + string.Join(" / ", menuPath.ConvertAll(item => item.name)) + " / " + (i + 1) + ": " + controlName;
                if (control.parameter != null)
                    CheckSetupMenuParameter(control.parameter.name, location + " / control parameter", false,
                        menu, expressionParameters, missingParameters, menuPath, control, valueHint: control.value);

                int axisCount = 0;
                switch (control.type)
                {
                    case VRCExpressionsMenu.Control.ControlType.RadialPuppet: axisCount = 1; break;
                    case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet: axisCount = 2; break;
                    case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet: axisCount = 4; break;
                }
                int storedAxes = control.subParameters != null ? control.subParameters.Length : 0;
                for (int axis = 0; axis < Math.Max(axisCount, storedAxes); axis++)
                {
                    var parameter = axis < storedAxes ? control.subParameters[axis] : null;
                    CheckSetupMenuParameter(parameter != null ? parameter.name : null, location + " / puppet axis " + (axis + 1),
                        axis < axisCount, menu, expressionParameters, missingParameters, menuPath, control);
                }

                if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                {
                    if (control.subMenu == null)
                        AddSetupIssue(MessageType.Warning, location + " has no submenu assigned.", menu);
                    else
                        ScanSetupMenu(control.subMenu, expressionParameters, visitedMenus, menuPath, missingParameters);
                }
            }
        }

        private void CheckSetupMenuParameter(string parameterName, string location, bool isPuppetAxis,
            VRCExpressionsMenu menu, Dictionary<string, VRCExpressionParameters.Parameter> expressionParameters,
            List<ParameterUsageEntry> missingParameters, List<VRCExpressionsMenu> menuPath, VRCExpressionsMenu.Control control, float? valueHint = null)
        {
            void AddMissing()
            {
                missingParameters.Add(new ParameterUsageEntry { kind = ParameterUsageKind.Menu, target = menu,
                    menuPath = new List<VRCExpressionsMenu>(menuPath), menuControl = control,
                    missingParameterName = parameterName, missingParameterTypes = isPuppetAxis ? SetupParameterTypes.Numeric : SetupParameterTypes.All,
                    missingParameterValue = valueHint,
                    location = (string.IsNullOrWhiteSpace(parameterName) ? "(Unassigned)" : parameterName) + " — " + location + " (VRC list)" });
            }
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                // The main activation parameter is optional; required puppet axes are not.
                if (isPuppetAxis) AddMissing();
                return;
            }
            if (VrcBuiltInParameterNames.Contains(parameterName))
            {
                AddSetupIssue(MessageType.Warning, location + " targets '" + parameterName + "', which is a read-only VRChat built-in. Use a custom parameter for menu controls.", menu);
                return;
            }
            if (_vrcExpressionParametersAsset == null) return;
            VRCExpressionParameters.Parameter parameter;
            if (!expressionParameters.TryGetValue(parameterName, out parameter))
            {
                AddMissing();
                return;
            }
            if (isPuppetAxis && parameter.valueType == VRCExpressionParameters.ValueType.Bool)
                AddSetupIssue(MessageType.Error, location + " uses Bool parameter '" + parameterName + "' for a puppet axis. The SDK requires a numeric parameter for this axis.", menu);
        }

        private static Motion GetSetupStateMotion(AnimatorController controller, AnimatorState state, int layer)
        {
            if (controller == null || state == null) return null;
            var layers = controller.layers;
            var visited = new HashSet<int>();
            while (layer >= 0 && layer < layers.Length && layers[layer] != null && visited.Add(layer))
            {
                if (layers[layer].syncedLayerIndex < 0) return state.motion;
                var motion = layers[layer].GetOverrideMotion(state);
                if (motion != null) return motion;
                layer = layers[layer].syncedLayerIndex;
            }
            return null;
        }

        private static bool IsMissingSetupMotionReference(SerializedProperty property)
        {
            // None has no reference ID; Missing retains the ID of an unavailable asset.
            return property != null && property.propertyType == SerializedPropertyType.ObjectReference &&
                property.objectReferenceValue == null && property.objectReferenceInstanceIDValue != 0;
        }

        private void ScanSetupAnimatorReferences(AnimatorController controller,
            Dictionary<string, AnimatorControllerParameterType> animatorParameters,
            Dictionary<string, VRCExpressionParameters.Parameter> expressionParameters,
            List<ParameterUsageEntry> missingMotions, List<ParameterUsageEntry> missingParameters)
        {
            var layers = controller.layers;
            var typeWarnings = new HashSet<string>(StringComparer.Ordinal);
            var missingOverrides = new Dictionary<int, HashSet<AnimatorState>>();
            using (var serializedController = new SerializedObject(controller))
            {
                var serializedLayers = serializedController.FindProperty("m_AnimatorLayers");
                if (serializedLayers != null && serializedLayers.isArray)
                {
                    for (int i = 0; i < layers.Length && i < serializedLayers.arraySize; i++)
                    {
                        if (layers[i] == null || layers[i].syncedLayerIndex < 0) continue;
                        var overrides = serializedLayers.GetArrayElementAtIndex(i).FindPropertyRelative("m_Motions");
                        if (overrides == null || !overrides.isArray) continue;
                        for (int j = 0; j < overrides.arraySize; j++)
                        {
                            var entry = overrides.GetArrayElementAtIndex(j);
                            var stateProperty = entry.FindPropertyRelative("m_State");
                            var state = stateProperty != null ? stateProperty.objectReferenceValue as AnimatorState : null;
                            if (state == null || !IsMissingSetupMotionReference(entry.FindPropertyRelative("m_Motion"))) continue;
                            if (!missingOverrides.TryGetValue(i, out var states))
                                missingOverrides[i] = states = new HashSet<AnimatorState>();
                            states.Add(state);
                        }
                    }
                }
            }

            Motion ResolveMotion(AnimatorState state, int layer, out bool missing)
            {
                missing = false;
                var visited = new HashSet<int>();
                while (layer >= 0 && layer < layers.Length && layers[layer] != null && visited.Add(layer))
                {
                    if (layers[layer].syncedLayerIndex < 0)
                    {
                        var motion = state.motion;
                        if (motion == null)
                        {
                            using (var serializedState = new SerializedObject(state))
                                missing = IsMissingSetupMotionReference(serializedState.FindProperty("m_Motion"));
                        }
                        return motion;
                    }
                    // A broken override must not fall back to the source layer's motion.
                    if (missingOverrides.TryGetValue(layer, out var states) && states.Contains(state))
                    {
                        missing = true;
                        return null;
                    }
                    var overrideMotion = layers[layer].GetOverrideMotion(state);
                    if (overrideMotion != null) return overrideMotion;
                    layer = layers[layer].syncedLayerIndex;
                }
                return null;
            }

            ParameterUsageEntry Location(string label, UnityEngine.Object target, int layer,
                AnimatorStateMachine machine, AnimatorState state)
            {
                return new ParameterUsageEntry { location = label, target = target, controller = controller,
                    kind = GetParameterUsageKind(target), layer = layer, layerName = layers[layer].name,
                    layerRoot = GetParameterUsageLayerRoot(controller, layer), machine = machine, state = state };
            }

            void CheckParameter(string name, string label, UnityEngine.Object target, int layer,
                AnimatorStateMachine machine, AnimatorState state, bool needsFloat = false, bool driver = false,
                SetupParameterTypes allowedTypes = SetupParameterTypes.All, float? valueHint = null)
            {
                bool empty = string.IsNullOrWhiteSpace(name);
                // Drivers may also refer to parameters supplied by the loaded expression list.
                if (driver && !empty && (expressionParameters.ContainsKey(name) || VrcBuiltInParameterNames.Contains(name))) return;
                AnimatorControllerParameterType type = default;
                if (empty || !animatorParameters.TryGetValue(name, out type))
                {
                    var location = Location((empty ? "(Unassigned)" : name) + " — " + label, target, layer, machine, state);
                    location.missingParameterName = name;
                    location.missingParameterTypes = needsFloat ? SetupParameterTypes.Float : allowedTypes;
                    location.missingParameterValue = valueHint;
                    missingParameters.Add(location);
                }
                else if (needsFloat && type != AnimatorControllerParameterType.Float && typeWarnings.Add(target.GetInstanceID() + ":" + name))
                    AddSetupIssue(MessageType.Warning, "Blend Tree '" + target.name + "' uses Animator parameter '" + name +
                        "' as " + type + ". Blend parameters should be Float in the Animator Controller.", target);
            }

            void ScanTransition(AnimatorTransitionBase transition, string path, int layer,
                AnimatorStateMachine machine, AnimatorState state)
            {
                if (transition == null) return;
                TrackSetupAsset(transition);
                string destination = transition.isExit ? "Exit" : transition.destinationState != null ? transition.destinationState.name :
                    transition.destinationStateMachine != null ? transition.destinationStateMachine.name : "Missing destination";
                var conditions = transition.conditions;
                for (int i = 0; i < conditions.Length; i++)
                    CheckParameter(conditions[i].parameter, path + " → " + destination + " / condition " + (i + 1),
                        transition, layer, machine, state, allowedTypes: GetSetupConditionParameterTypes(conditions[i].mode),
                        valueHint: conditions[i].mode == AnimatorConditionMode.If || conditions[i].mode == AnimatorConditionMode.IfNot
                            ? (float?)null : conditions[i].threshold);
            }

            void ScanDrivers(StateMachineBehaviour[] behaviours, string path, int layer, AnimatorStateMachine machine, AnimatorState state)
            {
                if (behaviours == null) return;
                foreach (var behaviour in behaviours)
                {
                    if (!(behaviour is VRC.SDKBase.VRC_AvatarParameterDriver)) continue;
                    TrackSetupAsset(behaviour);
                    using (var serialized = new SerializedObject(behaviour))
                    {
                        var entries = serialized.FindProperty("parameters");
                        if (entries == null || !entries.isArray) continue;
                        for (int i = 0; i < entries.arraySize; i++)
                        {
                            var entry = entries.GetArrayElementAtIndex(i);
                            var destination = entry.FindPropertyRelative("name");
                            var source = entry.FindPropertyRelative("source");
                            var type = entry.FindPropertyRelative("type");
                            string operation = type != null && type.propertyType == SerializedPropertyType.Enum && type.enumValueIndex >= 0 &&
                                type.enumValueIndex < type.enumNames.Length ? type.enumNames[type.enumValueIndex] : null;
                            var value = entry.FindPropertyRelative("value");
                            CheckParameter(destination != null ? destination.stringValue : null,
                                path + " / Parameter Driver destination " + (i + 1), behaviour, layer, machine, state, driver: true,
                                valueHint: operation == "Set" && value != null && value.propertyType == SerializedPropertyType.Float ? (float?)value.floatValue : null);
                            if (operation == "Copy")
                                CheckParameter(source != null ? source.stringValue : null,
                                    path + " / Parameter Driver copy source " + (i + 1), behaviour, layer, machine, state, driver: true);
                        }
                    }
                }
            }

            void ScanTree(BlendTree tree, string path, int layer, AnimatorStateMachine machine, AnimatorState state, HashSet<BlendTree> visited)
            {
                if (tree == null || !visited.Add(tree)) return;
                TrackSetupAsset(tree);
                string treePath = path + " / " + tree.name;
                if (tree.blendType != BlendTreeType.Direct)
                {
                    CheckParameter(tree.blendParameter, treePath + " / blend parameter X", tree, layer, machine, state, needsFloat: true);
                    if (Is2D(tree.blendType))
                        CheckParameter(tree.blendParameterY, treePath + " / blend parameter Y", tree, layer, machine, state, needsFloat: true);
                }
                var children = tree.children;
                using (var serializedTree = new SerializedObject(tree))
                {
                    var serializedChildren = serializedTree.FindProperty("m_Childs");
                    for (int i = 0; i < children.Length; i++)
                    {
                        var child = children[i];
                        string childPath = treePath + " / child " + (i + 1);
                        if (tree.blendType == BlendTreeType.Direct)
                            CheckParameter(child.directBlendParameter, childPath + " / direct weight", tree, layer, machine, state, needsFloat: true);
                        if (child.motion == null)
                        {
                            var motionProperty = serializedChildren != null && serializedChildren.isArray && i < serializedChildren.arraySize
                                ? serializedChildren.GetArrayElementAtIndex(i).FindPropertyRelative("m_Motion") : null;
                            if (IsMissingSetupMotionReference(motionProperty))
                                missingMotions.Add(Location(childPath + " — missing motion asset", tree, layer, machine, state));
                        }
                        else if (child.motion is BlendTree nested)
                            ScanTree(nested, childPath, layer, machine, state, visited);
                    }
                }
            }

            void ScanMachine(AnimatorStateMachine machine, string path, int layer, HashSet<AnimatorStateMachine> visited)
            {
                if (machine == null || !visited.Add(machine)) return;
                TrackSetupAsset(machine);
                ScanDrivers(machine.behaviours, path, layer, machine, null);
                var anyTransitions = machine.anyStateTransitions;
                for (int i = 0; i < anyTransitions.Length; i++)
                    ScanTransition(anyTransitions[i], path + " / Any State transition " + (i + 1), layer, machine, null);
                var entryTransitions = machine.entryTransitions;
                for (int i = 0; i < entryTransitions.Length; i++)
                    ScanTransition(entryTransitions[i], path + " / Entry transition " + (i + 1), layer, machine, null);
                foreach (var child in machine.states)
                {
                    var state = child.state;
                    if (state == null) continue;
                    TrackSetupAsset(state);
                    string statePath = path + " / " + state.name;
                    if (state.speedParameterActive) CheckParameter(state.speedParameter, statePath + " / speed", state, layer, machine, state, allowedTypes: SetupParameterTypes.Float);
                    if (state.timeParameterActive) CheckParameter(state.timeParameter, statePath + " / motion time", state, layer, machine, state, allowedTypes: SetupParameterTypes.Float);
                    if (state.cycleOffsetParameterActive) CheckParameter(state.cycleOffsetParameter, statePath + " / cycle offset", state, layer, machine, state, allowedTypes: SetupParameterTypes.Float);
                    if (state.mirrorParameterActive) CheckParameter(state.mirrorParameter, statePath + " / mirror", state, layer, machine, state, allowedTypes: SetupParameterTypes.Bool);
                    var transitions = state.transitions;
                    for (int i = 0; i < transitions.Length; i++)
                        ScanTransition(transitions[i], statePath + " / transition " + (i + 1), layer, machine, state);
                    ScanDrivers(controller.GetStateEffectiveBehaviours(state, layer), statePath, layer, machine, state);
                    var motion = ResolveMotion(state, layer, out bool missingMotion);
                    if (missingMotion) missingMotions.Add(Location(statePath + " — missing motion asset", state, layer, machine, state));
                    else if (motion is BlendTree tree) ScanTree(tree, statePath, layer, machine, state, new HashSet<BlendTree>());
                }
                foreach (var child in machine.stateMachines)
                {
                    if (child.stateMachine == null) continue;
                    string childPath = path + " / " + child.stateMachine.name;
                    var transitions = machine.GetStateMachineTransitions(child.stateMachine);
                    for (int i = 0; i < transitions.Length; i++)
                        ScanTransition(transitions[i], childPath + " / state machine transition " + (i + 1), layer, machine, null);
                    ScanMachine(child.stateMachine, childPath, layer, visited);
                }
            }

            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i] == null) continue;
                ScanMachine(GetParameterUsageLayerRoot(controller, i), "Layer " + i + ": " + layers[i].name,
                    i, new HashSet<AnimatorStateMachine>());
            }
        }

        private void ReorderVRCParameters(AnimatorController controller, SerializedProperty parametersProp)
        {
            if (controller == null || parametersProp == null || !parametersProp.isArray) return;

            var names = new string[parametersProp.arraySize];
            var currentOrder = new List<int>(names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = parametersProp.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue;
                currentOrder.Add(i);
            }
            var desiredOrder = new List<int>(names.Length);
            var included = new HashSet<int>();
            foreach (var row in BuildParameterOrder(controller.parameters, names))
                if (row.vrcIndex >= 0 && included.Add(row.vrcIndex)) desiredOrder.Add(row.vrcIndex);
            // Retain every serialized entry, including any invalid duplicate names.
            for (int i = 0; i < names.Length; i++)
                if (included.Add(i)) desiredOrder.Add(i);
            for (int i = 0; i < desiredOrder.Count; i++)
            {
                int currentIndex = currentOrder.IndexOf(desiredOrder[i], i);
                if (currentIndex == i) continue;
                parametersProp.MoveArrayElement(currentIndex, i);
                currentOrder.RemoveAt(currentIndex);
                currentOrder.Insert(i, desiredOrder[i]);
            }
        }

        private bool StateHasWDWarning(AnimatorState state)
        {
            if (state == null || state.writeDefaultValues) return false;
            return state.motion is BlendTree bt && bt.blendType == BlendTreeType.Direct;
        }

        private bool StateMachineHasWDWarning(AnimatorStateMachine sm)
        {
            if (sm == null) return false;
            foreach (var childState in sm.states)
            {
                if (StateHasWDWarning(childState.state)) return true;
            }
            foreach (var childSm in sm.stateMachines)
            {
                if (StateMachineHasWDWarning(childSm.stateMachine)) return true;
            }
            return false;
        }

        private HashSet<string> GetNonFloatParameters(AnimatorController controller)
        {
            HashSet<string> nonFloats = new HashSet<string>();
            if (controller != null)
            {
                foreach (var p in controller.parameters)
                {
                    if (p.type != AnimatorControllerParameterType.Float) nonFloats.Add(p.name);
                }
            }
            return nonFloats;
        }

        private bool BlendTreeHasParamWarning(BlendTree tree, HashSet<string> nonFloatParams)
        {
            return BlendTreeHasParamWarning(tree, nonFloatParams, new HashSet<BlendTree>());
        }

        private bool BlendTreeHasParamWarning(BlendTree tree, HashSet<string> nonFloatParams, HashSet<BlendTree> visitedTrees)
        {
            if (tree == null || !visitedTrees.Add(tree)) return false;
            if (tree.blendType != BlendTreeType.Direct && nonFloatParams.Contains(tree.blendParameter)) return true;
            if (Is2D(tree.blendType) && nonFloatParams.Contains(tree.blendParameterY)) return true;
            foreach (var child in tree.children)
            {
                if (tree.blendType == BlendTreeType.Direct && nonFloatParams.Contains(child.directBlendParameter)) return true;
                if (child.motion is BlendTree subTree)
                {
                    if (BlendTreeHasParamWarning(subTree, nonFloatParams, visitedTrees)) return true;
                }
            }
            return false;
        }

        private bool StateHasParamWarning(AnimatorState state, HashSet<string> nonFloatParams)
        {
            return StateHasParamWarning(state, nonFloatParams, new HashSet<BlendTree>());
        }

        private bool StateHasParamWarning(AnimatorState state, HashSet<string> nonFloatParams, HashSet<BlendTree> visitedTrees)
        {
            if (state == null) return false;
            if (state.speedParameterActive && nonFloatParams.Contains(state.speedParameter)) return true;
            if (state.timeParameterActive && nonFloatParams.Contains(state.timeParameter)) return true;
            if (state.cycleOffsetParameterActive && nonFloatParams.Contains(state.cycleOffsetParameter)) return true;

            if (state.motion is BlendTree bt)
            {
                if (BlendTreeHasParamWarning(bt, nonFloatParams, visitedTrees)) return true;
            }
            return false;
        }

        private bool StateMachineHasParamWarning(AnimatorStateMachine sm, HashSet<string> nonFloatParams)
        {
            return StateMachineHasParamWarning(sm, nonFloatParams, new HashSet<BlendTree>());
        }

        private bool StateMachineHasParamWarning(AnimatorStateMachine sm, HashSet<string> nonFloatParams, HashSet<BlendTree> visitedTrees)
        {
            if (sm == null) return false;
            foreach (var childState in sm.states)
            {
                if (StateHasParamWarning(childState.state, nonFloatParams, visitedTrees)) return true;
            }
            foreach (var childSm in sm.stateMachines)
            {
                if (StateMachineHasParamWarning(childSm.stateMachine, nonFloatParams, visitedTrees)) return true;
            }
            return false;
        }

        private void InvalidateLayerSummaries()
        {
            _layerSummaryController = null;
            _nextLayerSummaryRefresh = 0;
            _nextStateSearchRefresh = 0;
            _nextIncomingTransitionRefresh = 0;
        }

        private void SelectLayer(AnimatorController controller, int index)
        {
            var layers = controller.layers;
            _selectedLayerIndex = index >= 0 && index < layers.Length ? index : -1;
            _layerSelectionStateMachine = _selectedLayerIndex >= 0 ? layers[_selectedLayerIndex].stateMachine : null;
            _layerSelectionName = _selectedLayerIndex >= 0 ? layers[_selectedLayerIndex].name : null;
            ResetStateMachineNavigation();
            if (_cachedBehaviourEditor != null) DestroyImmediate(_cachedBehaviourEditor);
            _cachedBehaviourEditor = null;
            GUI.FocusControl(null);
        }

        private void RefreshLayerSummaries(AnimatorController controller, AnimatorControllerLayer[] layers, HashSet<string> nonFloatParams)
        {
            if (_layerSummaryController == controller && _layerSummaries.Count == layers.Length &&
                (Event.current.type != EventType.Layout || EditorApplication.timeSinceStartup < _nextLayerSummaryRefresh)) return;
            _layerSummaryController = controller;
            _nextLayerSummaryRefresh = EditorApplication.timeSinceStartup + 1;
            _layerSummaries.Clear();
            _layerStateParameterWarnings.Clear();
            foreach (var layer in layers)
            {
                var summary = new LayerSummary();
                var search = new System.Text.StringBuilder(layer.name);
                var machines = new Stack<AnimatorStateMachine>();
                var visited = new HashSet<AnimatorStateMachine>();
                var visitedMotions = new HashSet<Motion>();
                // A synced layer displays its source's structure; overrides are edited in Unity's Animator.
                int source = layer.syncedLayerIndex;
                AnimatorStateMachine root = source >= 0 && source < layers.Length ? layers[source].stateMachine : layer.stateMachine;
                if (root != null) machines.Push(root);
                while (machines.Count > 0)
                {
                    var machine = machines.Pop();
                    if (machine == null || !visited.Add(machine)) continue;
                    search.Append('\n').Append(machine.name);
                    foreach (var behaviour in machine.behaviours)
                        if (behaviour != null && behaviour.GetType().Name == "VRCAnimatorLayerControl") summary.hasLayerControls = true;
                    foreach (var child in machine.states)
                    {
                        var state = child.state;
                        if (state == null) continue;
                        summary.stateCount++;
                        search.Append('\n').Append(state.name);
                        AppendLayerMotionNames(state.motion, search, visitedMotions);
                        summary.writeDefaultsWarning |= StateHasWDWarning(state);
                        bool parameterWarning = StateHasParamWarning(state, nonFloatParams);
                        _layerStateParameterWarnings[state] = parameterWarning;
                        summary.parameterWarning |= parameterWarning;
                        foreach (var behaviour in state.behaviours)
                            if (behaviour != null && behaviour.GetType().Name == "VRCAnimatorLayerControl") summary.hasLayerControls = true;
                    }
                    foreach (var child in machine.stateMachines)
                        if (child.stateMachine != null) machines.Push(child.stateMachine);
                }
                if (source >= 0)
                {
                    summary.writeDefaultsWarning = false;
                    summary.parameterWarning = false;
                }
                summary.searchText = search.ToString();
                _layerSummaries.Add(summary);
            }
        }

        private static void AppendLayerMotionNames(Motion motion, System.Text.StringBuilder search, HashSet<Motion> visited)
        {
            if (motion == null || !visited.Add(motion)) return;
            search.Append('\n').Append(motion.name);
            var tree = motion as BlendTree;
            if (tree == null) return;
            foreach (var child in tree.children) AppendLayerMotionNames(child.motion, search, visited);
        }

        private void HandleOtherDragCancellation()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown || e.keyCode != KeyCode.Escape ||
                (!_isParamDragging && !_isDragging && !_isMenuDragging)) return;

            CancelParameterDrag();
            CancelMotionDrag();
            CancelMenuDrag();
            GUI.FocusControl(null);
            e.Use();
            Repaint();
            GUIUtility.ExitGUI();
        }

        private void ResetParameterDrag()
        {
            _isParamDragging = false;
            _paramDragSourceIndex = -1;
            _paramDragController = null;
            _paramDragOriginalParameters = null;
            _paramDragOriginalSelection = -1;
            _paramDragHasMoved = false;
        }

        private void CancelParameterDrag()
        {
            if (_paramDragHasMoved && _paramDragController != null && _paramDragOriginalParameters != null)
            {
                _paramDragController.parameters = _paramDragOriginalParameters;
                EditorUtility.SetDirty(_paramDragController);
                if (_paramDragController == GetAnimatorController()) _selectedParamIndex = _paramDragOriginalSelection;
                _hasParamCache = false;
            }
            ResetParameterDrag();
        }

        private void ResetMotionDrag()
        {
            _isDragging = false;
            _dragSourceIndex = -1;
            _motionDragTree = null;
            _motionDragOriginalChildren = null;
            _motionDragHasMoved = false;
            _motionDragOriginalCutIndices = null;
        }

        private void CancelMotionDrag()
        {
            if (_motionDragHasMoved && _motionDragTree != null && _motionDragOriginalChildren != null)
            {
                _motionDragTree.children = _motionDragOriginalChildren;
                EditorUtility.SetDirty(_motionDragTree);
                if (_motionDragOriginalCutIndices != null)
                    foreach (var cut in _motionDragOriginalCutIndices)
                        if (_motionCuts.Contains(cut.Key))
                        {
                            cut.Key.index = cut.Value;
                            cut.Key.original = _motionDragTree.children[cut.Value];
                            cut.Key.invalid = false;
                        }
            }
            ResetMotionDrag();
        }

        private void ResetMenuDrag()
        {
            if (_menuDragControlId != 0 && GUIUtility.hotControl == _menuDragControlId)
                GUIUtility.hotControl = 0;
            _isMenuDragging = false;
            _menuDragSourceIndex = _menuDropTarget = -1;
            _menuDragOwner = null;
            _menuDragOriginalControls = null;
            _menuDragHasMoved = false;
            _menuDragControlId = 0;
            _menuDragPointerOffset = 0f;
            _menuRowRects.Clear();
            _menuRowIndices.Clear();
        }

        private void CancelMenuDrag()
        {
            if (_menuDragHasMoved && _menuDragOwner != null && _menuDragOriginalControls != null)
            {
                _menuDragOwner.controls = new List<VRCExpressionsMenu.Control>(_menuDragOriginalControls);
                if (_selectedMenuOwner == _menuDragOwner)
                    _selectedMenuControlIndex = _menuDragOwner.controls.IndexOf(_selectedMenuControlReference);
                MarkMenuChanged(_menuDragOwner);
            }
            ResetMenuDrag();
        }

        private int FindMenuDragTarget(float pointerY)
        {
            int sourceRow = _menuRowIndices.IndexOf(_menuDragSourceIndex);
            if (sourceRow < 0) return -1;

            Rect sourceRect = _menuRowRects[sourceRow];
            float draggedCenter = pointerY - _menuDragPointerOffset + sourceRect.height * 0.5f;
            int targetRow = sourceRow;
            // Cross the next row's midpoint before moving. This prevents rows of different
            // heights (such as submenus and toggles) from swapping back under a still pointer.
            if (draggedCenter > sourceRect.center.y)
            {
                for (int row = sourceRow + 1; row < _menuRowRects.Count; row++)
                    if (draggedCenter > _menuRowRects[row].center.y) targetRow = row;
            }
            else
            {
                for (int row = sourceRow - 1; row >= 0; row--)
                    if (draggedCenter < _menuRowRects[row].center.y) targetRow = row;
            }
            return _menuRowIndices[targetRow];
        }

        private void ResetLayerDrag()
        {
            if (_layerDragControlId != 0 && GUIUtility.hotControl == _layerDragControlId)
                GUIUtility.hotControl = 0;
            _isLayerDragging = false;
            _layerDragSourceIndex = _layerDropTarget = _layerDragStartIndex = -1;
            _layerDragControlId = 0;
            _layerDragHasMoved = false;
        }

        private bool MoveEditorLayer(AnimatorController controller, int from, int to, bool recordUndo = true)
        {
            var layers = controller.layers;
            if (from < 0 || to < 0 || from >= layers.Length || to >= layers.Length || from == to) return false;
            var order = new List<int>();
            for (int i = 0; i < layers.Length; i++) order.Add(i);
            order.RemoveAt(from);
            order.Insert(to, from);
            var indexMap = new int[layers.Length];
            for (int i = 0; i < order.Count; i++) indexMap[order[i]] = i;
            var reordered = new AnimatorControllerLayer[layers.Length];
            if (recordUndo) Undo.RegisterCompleteObjectUndo(controller, "Move Animator Layer");
            for (int i = 0; i < reordered.Length; i++)
            {
                var layer = layers[order[i]];
                int sync = layer.syncedLayerIndex;
                if (sync >= 0 && sync < indexMap.Length) layer.syncedLayerIndex = indexMap[sync];
                reordered[i] = layer;
            }
            controller.layers = reordered;
            if (_selectedLayerIndex >= 0 && _selectedLayerIndex < indexMap.Length)
                _selectedLayerIndex = indexMap[_selectedLayerIndex];
            EditorUtility.SetDirty(controller);
            // The summaries still describe the same layers. Reorder the cache during the drag
            // instead of traversing every state machine again at each crossed row.
            if (_layerSummaryController == controller && _layerSummaries.Count == layers.Length)
            {
                var summary = _layerSummaries[from];
                _layerSummaries.RemoveAt(from);
                _layerSummaries.Insert(to, summary);
            }
            else InvalidateLayerSummaries();
            _hasParamCache = false;
            return true;
        }

        private void DeleteEditorLayer(AnimatorController controller, int index)
        {
            var layers = controller.layers;
            if (index <= 0 || index >= layers.Length) return;
            for (int i = 0; i < layers.Length; i++)
            {
                if (i != index && layers[index].syncedLayerIndex < 0 && layers[index].stateMachine != null &&
                    layers[i].stateMachine == layers[index].stateMachine)
                {
                    EditorUtility.DisplayDialog("Shared State Machine",
                        "'" + layers[i].name + "' shares this layer's state machine. Separate the shared state machine in Unity's Animator before deleting this layer.", "OK");
                    return;
                }
                if (layers[i].syncedLayerIndex != index) continue;
                EditorUtility.DisplayDialog("Layer Is Used for Sync",
                    "'" + layers[i].name + "' synchronizes with this layer. Change its sync source in Unity's Animator before deleting '" + layers[index].name + "'.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Delete Layer", "Delete '" + layers[index].name + "' and its state machine? You can undo this change.", "Delete", "Cancel")) return;
            Undo.RegisterCompleteObjectUndo(controller, "Delete Animator Layer");
            controller.RemoveLayer(index);
            var remaining = controller.layers;
            for (int i = 0; i < remaining.Length; i++)
            {
                int oldIndex = i < index ? i : i + 1;
                int oldSync = layers[oldIndex].syncedLayerIndex;
                remaining[i].syncedLayerIndex = oldSync > index ? oldSync - 1 : oldSync;
            }
            controller.layers = remaining;
            if (_selectedLayerIndex == index) SelectLayer(controller, -1);
            else if (_selectedLayerIndex > index) _selectedLayerIndex--;
            EditorUtility.SetDirty(controller);
            InvalidateLayerSummaries();
            _hasParamCache = false;
            RefreshAvailableTrees();
        }

        private void DrawLayerEditorSection(AnimatorController controller)
        {
            if (!_showLayerEditor) { ResetLayerDrag(); return; }
            GUILayout.Space(5);
            EditorGUILayout.LabelField("Layer Editor", EditorStyles.boldLabel);

            var layers = controller.layers;
            if (_layerEditorController != controller)
            {
                _layerEditorController = controller;
                _layerSearchQuery = "";
                ResetLayerDrag();
                SelectLayer(controller, -1);
                InvalidateLayerSummaries();
            }
            // Follow the selected layer when another editor reorders it, and discard deleted selections.
            if (_selectedLayerIndex >= 0)
            {
                int selected = Array.FindIndex(layers, layer => layer.stateMachine == _layerSelectionStateMachine && layer.name == _layerSelectionName);
                if (selected < 0 && _layerSelectionStateMachine != null)
                    selected = Array.FindIndex(layers, layer => layer.stateMachine == _layerSelectionStateMachine);
                if (selected < 0) SelectLayer(controller, -1);
                else _selectedLayerIndex = selected;
            }
            var nonFloatParams = GetNonFloatParameters(controller);
            RefreshLayerSummaries(controller, layers, nonFloatParams);
            Event e = Event.current;
            bool addLayer = false;
            int moveFrom = -1, moveTo = -1, deleteIndex = -1;
            int layerToExpand = -2;
            bool finishLayerDrag = false, cancelLayerDrag = false;

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            const string layerSearchTooltip = "Search layer names, nested states, sub-state machines and motion names.";
            GUILayout.Label(new GUIContent("Layers (" + layers.Length + ")", layerSearchTooltip), EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
            bool compactToolbar = position.width >= 700f;
            if (_layerSummaries.Exists(summary => summary.hasLayerControls))
                GUILayout.Label(new GUIContent("!", "This controller contains Animator Layer Control behaviours. After reordering or deleting layers, check the numbered layer targets in those behaviours."), EditorStyles.boldLabel, GUILayout.Width(12));
            if (!compactToolbar)
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Open Animator", GUILayout.Width(105))) AssetDatabase.OpenAsset(controller);
                if (GUILayout.Button("+ Add Layer", GUILayout.Width(100))) addLayer = true;
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent("Find layer or state", layerSearchTooltip), GUILayout.ExpandWidth(false));
            }
            EditorGUI.BeginChangeCheck();
            _layerSearchQuery = EditorGUILayout.TextField(_layerSearchQuery, EditorStyles.toolbarSearchField,
                GUILayout.MinWidth(70), GUILayout.ExpandWidth(true));
            if (EditorGUI.EndChangeCheck()) _layerScrollPos = Vector2.zero;
            if (GUILayout.Button("Clear", GUILayout.Width(45))) { _layerSearchQuery = ""; GUI.FocusControl(null); }
            if (compactToolbar)
            {
                if (GUILayout.Button("Open Animator", GUILayout.Width(105))) AssetDatabase.OpenAsset(controller);
                if (GUILayout.Button("+ Add Layer", GUILayout.Width(100))) addLayer = true;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            DrawSetupWriteDefaultsOptions(controller, "LayerWriteDefaults");
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            bool searching = !string.IsNullOrWhiteSpace(_layerSearchQuery);
            if (searching || (_isLayerDragging && GUIUtility.hotControl != _layerDragControlId)) ResetLayerDrag();
            int layerDragControl = GUIUtility.GetControlID(0x4C415952, FocusType.Passive);
            int visible = 0;
            Vector2 layerPointer = e.mousePosition;
            bool showLayerDetails = !_isLayerDragging;
            using var layerScrollView = new NestedScrollViewScope(ref _layerScrollPos, GUILayout.Height(_layerListHeight));
            if (e.type == EventType.Repaint) { _layerRowRects.Clear(); _layerRowIndices.Clear(); }
            for (int i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                var summary = _layerSummaries[i];
                if (searching && summary.searchText.IndexOf(_layerSearchQuery.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                visible++;
                Color background = GUI.backgroundColor;
                if (i == _selectedLayerIndex) GUI.backgroundColor = new Color(0.4f, 0.7f, 1f);
                if (_isLayerDragging && i == _layerDragSourceIndex) GUI.backgroundColor = new Color(0.3f, 0.7f, 1f, 1f);
                EditorGUILayout.BeginHorizontal("box", GUILayout.Height(26));
                GUILayout.Label(new GUIContent("≡", searching ? "Clear search to reorder layers." : ReorderDragTooltip), GUILayout.Width(18));
                Rect handle = GUILayoutUtility.GetLastRect();
                if (!searching)
                {
                    EditorGUIUtility.AddCursorRect(handle, MouseCursor.Pan);
                    if (e.type == EventType.MouseDown && e.button == 0 && _layerViewportRect.Contains(layerPointer) && handle.Contains(e.mousePosition))
                    {
                        _isLayerDragging = true;
                        _layerDragSourceIndex = i;
                        _layerDragStartIndex = i;
                        _layerDragHasMoved = false;
                        _layerDropTarget = i;
                        _layerDragControlId = layerDragControl;
                        GUIUtility.hotControl = layerDragControl;
                        GUI.FocusControl(null);
                        _layerRowRects.Clear();
                        _layerRowIndices.Clear();
                        Repaint();
                        e.Use();
                    }
                }
                GUILayout.Label(i.ToString(), EditorStyles.miniLabel, GUILayout.Width(23));
                if (GUILayout.Button(_selectedLayerIndex == i ? "▼" : "►", EditorStyles.miniButton, GUILayout.Width(24)))
                    layerToExpand = _selectedLayerIndex == i ? -1 : i;
                if (GUILayout.Button(new GUIContent(layer.name, layer.name), EditorStyles.miniButton, GUILayout.MinWidth(80), GUILayout.ExpandWidth(true)))
                    layerToExpand = _selectedLayerIndex == i ? -1 : i;
                string summaryText = summary.stateCount + " states" + (layer.syncedLayerIndex >= 0 ? " (sync)" : "");
                GUILayout.Label(summaryText, EditorStyles.miniLabel, GUILayout.Width(98));
                if (summary.writeDefaultsWarning || summary.parameterWarning)
                    GUILayout.Label(new GUIContent("!", "This layer contains state warnings. Select it to inspect its states."), EditorStyles.boldLabel, GUILayout.Width(12));
                using (new EditorGUI.DisabledScope(i == 0))
                    if (GUILayout.Button(new GUIContent("X", "Delete layer"), GUILayout.Width(24))) deleteIndex = i;
                EditorGUILayout.EndHorizontal();
                GUI.backgroundColor = background;
                if (e.type == EventType.Repaint)
                {
                    _layerRowRects.Add(GUILayoutUtility.GetLastRect());
                    _layerRowIndices.Add(i);
                }
                if (_selectedLayerIndex == i && showLayerDetails)
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    DrawLayerInlineDetails(controller, layers, i, nonFloatParams, ref layerToExpand);
                    EditorGUILayout.EndVertical();
                    GUILayout.Space(4);
                }
            }
            if (visible == 0) EditorGUILayout.HelpBox(searching ? "No layers or states match this search." : "Add a layer to get started.", MessageType.Info);
            if (_isLayerDragging)
            {
                if (e.type == EventType.MouseDrag || (e.rawType == EventType.MouseUp && e.button == 0))
                {
                    _layerDropTarget = -1;
                    if (_layerViewportRect.Contains(layerPointer))
                    {
                        for (int row = 0; row < _layerRowRects.Count; row++)
                            if (_layerRowRects[row].Contains(e.mousePosition)) _layerDropTarget = _layerRowIndices[row];
                    }
                    if (_layerDropTarget >= 0 && _layerDropTarget != _layerDragSourceIndex)
                    {
                        moveFrom = _layerDragSourceIndex;
                        moveTo = _layerDropTarget;
                    }
                    finishLayerDrag = e.rawType == EventType.MouseUp;
                    Repaint();
                    e.Use();
                }
                else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                {
                    finishLayerDrag = cancelLayerDrag = true;
                    Repaint();
                    e.Use();
                }
            }
            layerScrollView.Dispose();
            if (e.type == EventType.Repaint)
            {
                _layerViewportRect = GUILayoutUtility.GetLastRect();
                _layerViewportScreenRect = new Rect(GUIUtility.GUIToScreenPoint(_layerViewportRect.position), _layerViewportRect.size);
            }
            DrawLayerListResizer();

            EditorGUILayout.EndVertical();

            // Mutate the layer array after drawing so this event never uses a stale snapshot.
            if (cancelLayerDrag)
            {
                if (_layerDragHasMoved)
                    MoveEditorLayer(controller, _layerDragSourceIndex, _layerDragStartIndex, false);
            }
            else if (addLayer)
            {
                Undo.RegisterCompleteObjectUndo(controller, "Add Animator Layer");
                controller.AddLayer(GetUniqueLayerName(controller, "New Layer"));
                var updated = controller.layers;
                updated[updated.Length - 1].defaultWeight = 1;
                controller.layers = updated;
                EditorUtility.SetDirty(controller);
                _layerSearchQuery = "";
                _layerScrollPos = new Vector2(0, float.MaxValue);
                SelectLayer(controller, updated.Length - 1);
                InvalidateLayerSummaries();
            }
            else if (deleteIndex >= 0) DeleteEditorLayer(controller, deleteIndex);
            else if (moveFrom >= 0 && moveTo >= 0)
            {
                // Record the starting order once so one Undo restores the whole drag.
                if (MoveEditorLayer(controller, moveFrom, moveTo, !_layerDragHasMoved))
                {
                    _layerDragSourceIndex = moveTo;
                    _layerDragHasMoved = true;
                }
            }
            else if (layerToExpand != -2)
            {
                SelectLayer(controller, layerToExpand);
                Repaint();
            }
            if (finishLayerDrag) ResetLayerDrag();
        }

        private void DrawLayerInlineDetails(AnimatorController controller, AnimatorControllerLayer[] layers,
            int index, HashSet<string> nonFloatParams, ref int layerToExpand)
        {
            var layer = layers[index];
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(42));
            string name = EditorGUILayout.DelayedTextField(layer.name, GUILayout.MinWidth(70), GUILayout.ExpandWidth(true));
            GUILayout.Label("Mask", GUILayout.Width(34));
            AvatarMask mask = (AvatarMask)EditorGUILayout.ObjectField(layer.avatarMask, typeof(AvatarMask), false,
                GUILayout.MinWidth(70), GUILayout.ExpandWidth(true));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Blending", GUILayout.Width(54));
            AnimatorLayerBlendingMode blend = (AnimatorLayerBlendingMode)HubEnumPopup("LayerBlend_" + controller.GetInstanceID() + "_" + index,
                null, layer.blendingMode, GUILayout.MinWidth(75));
            bool ikPass = EditorGUILayout.ToggleLeft("IK Pass", layer.iKPass, GUILayout.Width(68));
            GUILayout.Label(new GUIContent("Weight", "Default layer weight. The base layer always uses 1."), GUILayout.Width(46));
            float weight = layer.defaultWeight;
            using (new EditorGUI.DisabledScope(index == 0))
                weight = EditorGUILayout.Slider(index == 0 ? 1f : weight, 0, 1, GUILayout.MinWidth(70));
            EditorGUILayout.EndHorizontal();
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RegisterCompleteObjectUndo(controller, "Edit Animator Layer");
                if (!string.IsNullOrWhiteSpace(name)) layer.name = name;
                layer.avatarMask = mask;
                layer.blendingMode = blend;
                layer.iKPass = ikPass;
                if (index != 0) layer.defaultWeight = weight;
                controller.layers = layers;
                _layerSelectionName = layer.name;
                EditorUtility.SetDirty(controller);
                InvalidateLayerSummaries();
            }
            GuiLine();
            if (layer.syncedLayerIndex >= 0)
            {
                int source = layer.syncedLayerIndex;
                string sourceName = source < layers.Length ? layers[source].name : "Missing layer";
                EditorGUILayout.HelpBox("Synchronized with: " + sourceName + ". Edit motion and behaviour overrides in Unity's Animator.", MessageType.Info);
                using (new EditorGUI.DisabledScope(source < 0 || source >= layers.Length || source == index))
                    if (GUILayout.Button("Select Source Layer")) layerToExpand = source;
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                _stateSearchQuery = EditorGUILayout.TextField(new GUIContent("Find state or motion", "Search every nested state in this layer."), _stateSearchQuery, EditorStyles.toolbarSearchField);
                if (GUILayout.Button("Clear", GUILayout.Width(45))) { _stateSearchQuery = ""; GUI.FocusControl(null); }
                EditorGUILayout.EndHorizontal();
                EditorGUI.BeginChangeCheck();
                DrawStateMachineEditor(controller, layer.stateMachine, _stateSearchQuery, nonFloatParams);
                if (EditorGUI.EndChangeCheck()) InvalidateLayerSummaries();
            }
        }

        private void DrawLayerListResizer()
        {
            Event e = Event.current;
            Rect rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(6), GUILayout.ExpandWidth(true));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);
            if (e.type == EventType.Repaint) EditorGUI.DrawRect(new Rect(rect.x, rect.y + 2, rect.width, 2), new Color(0.15f, 0.15f, 0.15f));
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition)) { _isResizingLayerList = true; e.Use(); }
            if (_isResizingLayerList && e.type == EventType.MouseDrag)
            {
                _layerListHeight = Mathf.Clamp(_layerListHeight + e.delta.y, 60, 1000);
                Repaint();
                e.Use();
            }
            if (_isResizingLayerList && e.rawType == EventType.MouseUp)
            {
                _isResizingLayerList = false;
                EditorPrefs.SetFloat(PrefLayerListHeightKey, _layerListHeight);
                e.Use();
            }
        }


        private void ResetStateMachineNavigation()
        {
            _stateMachineNavigation.Clear();
            _stateBreadcrumbScrollPosition = Vector2.zero;
            _stateBrowserScrollPosition = Vector2.zero;
            _selectedState = null;
            _selectedTransition = null;
            _selectedIncomingTransition = null;
            _expandedLayerBehaviour = null;
            _stateSearchQuery = "";
            _nextStateSearchRefresh = 0;
        }

        private void DrawStateMachineEditor(AnimatorController controller, AnimatorStateMachine sm, string searchQuery, HashSet<string> nonFloatParams)
        {
            if (controller == null || sm == null)
            {
                EditorGUILayout.HelpBox("This layer does not have a state machine.", MessageType.Info);
                return;
            }

            ValidateStateMachineNavigation(sm);
            AnimatorStateMachine currentMachine = _stateMachineNavigation[_stateMachineNavigation.Count - 1];
            AnimatorStateMachine navigateTo = null;
            AnimatorState stateToSelect = null;
            bool clearStateSelection = false;
            bool createEmptyState = false;
            List<Motion> motionsToAdd = null;
            AnimatorState stateToDelete = null;
            AnimatorStateMachine deleteOwner = null;
            AnimatorState newDefaultState = null;
            AnimatorStateMachine defaultOwner = null;
            string query = (searchQuery ?? "").Trim();
            bool isSearching = query.Length > 0;

            // A scrollable breadcrumb keeps deep state-machine paths usable in narrow windows.
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(_stateMachineNavigation.Count <= 1);
            if (GUILayout.Button(new GUIContent("Up", "Go to the parent state machine."), GUILayout.Width(35)))
                navigateTo = _stateMachineNavigation[_stateMachineNavigation.Count - 2];
            EditorGUI.EndDisabledGroup();
            using var stateBreadcrumbScrollView = new NestedScrollViewScope(ref _stateBreadcrumbScrollPosition, GUILayout.Height(38));
            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < _stateMachineNavigation.Count; i++)
            {
                if (i > 0) GUILayout.Label("/", GUILayout.Width(8));
                AnimatorStateMachine item = _stateMachineNavigation[i];
                string name = i == 0 ? "Layer Root" : item.name;
                if (GUILayout.Button(new GUIContent(name, item.name), EditorStyles.miniButton,
                    GUILayout.MaxWidth(160), GUILayout.ExpandWidth(false))) navigateTo = item;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            stateBreadcrumbScrollView.Dispose();
            EditorGUILayout.EndHorizontal();

            var states = new List<LayerStateEntry>();
            var machines = new List<LayerMachineEntry>();
            if (isSearching)
            {
                if (_stateSearchRoot != sm || _stateSearchText != query ||
                    (Event.current.type == EventType.Layout && EditorApplication.timeSinceStartup >= _nextStateSearchRefresh))
                {
                    _cachedStateSearch.Clear();
                    _cachedMachineSearch.Clear();
                    CollectLayerStateSearch(sm, sm.name, query, _cachedStateSearch, _cachedMachineSearch, new HashSet<AnimatorStateMachine>());
                    _stateSearchRoot = sm;
                    _stateSearchText = query;
                    _nextStateSearchRefresh = EditorApplication.timeSinceStartup + 1;
                }
                states = _cachedStateSearch;
                machines = _cachedMachineSearch;
                GUILayout.Label($"Search results ({states.Count} states, {machines.Count} folders)", EditorStyles.boldLabel);
            }
            else
            {
                foreach (var child in currentMachine.stateMachines)
                {
                    if (child.stateMachine != null && !_stateMachineNavigation.Contains(child.stateMachine))
                        machines.Add(new LayerMachineEntry { machine = child.stateMachine, path = child.stateMachine.name });
                }
                foreach (var child in currentMachine.states)
                {
                    if (child.state != null)
                        states.Add(new LayerStateEntry { owner = currentMachine, state = child.state, path = currentMachine.name });
                }
                GUILayout.Label($"States ({states.Count}) · Sub-State Machines ({machines.Count})", EditorStyles.boldLabel);
            }

            Rect creationRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            Event creationEvent = Event.current;
            if ((creationEvent.type == EventType.DragUpdated || creationEvent.type == EventType.DragPerform) &&
                creationRect.Contains(creationEvent.mousePosition) && _layerViewportScreenRect.Contains(GUIUtility.GUIToScreenPoint(creationEvent.mousePosition)))
            {
                var dropped = new List<Motion>();
                foreach (var obj in DragAndDrop.objectReferences)
                    if (obj is Motion motion && !dropped.Contains(motion)) dropped.Add(motion);
                if (dropped.Count > 0)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (creationEvent.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        motionsToAdd = dropped;
                    }
                    creationEvent.Use();
                }
            }
            Rect emptyRect = new Rect(creationRect.xMax - 100, creationRect.y, 100, creationRect.height);
            Rect fromMotionRect = new Rect(emptyRect.x - 104, creationRect.y, 100, creationRect.height);
            Rect motionRect = new Rect(creationRect.x, creationRect.y, Mathf.Max(30, fromMotionRect.x - creationRect.x - 4), creationRect.height);
            _layerMotionToAdd = (Motion)EditorGUI.ObjectField(motionRect, _layerMotionToAdd, typeof(Motion), false);
            GUI.Label(motionRect, new GUIContent("", "Choose a motion, or drop multiple animation clips/BlendTrees anywhere on this row to create states in " + currentMachine.name + "."), GUIStyle.none);
            using (new EditorGUI.DisabledScope(_layerMotionToAdd == null))
                if (GUI.Button(fromMotionRect, "+ From Motion")) motionsToAdd = new List<Motion> { _layerMotionToAdd };
            if (GUI.Button(emptyRect, "+ Empty State")) createEmptyState = true;

            if (states.Count == 0 && machines.Count == 0)
            {
                EditorGUILayout.HelpBox(isSearching
                    ? "No matches. Search checks state names, motion names, and sub-state-machine paths throughout this layer."
                    : "This state machine is empty. Add an animation clip or create an empty state.", MessageType.Info);
            }
            else
            {
                bool hasExpandedState = false;
                foreach (LayerStateEntry item in states)
                {
                    if (item.state != null && item.state == _selectedState && item.owner != null)
                    {
                        hasExpandedState = true;
                        break;
                    }
                }
                float listHeight = Mathf.Clamp(machines.Count * 27f + states.Count * 27f + 8f
                    + (hasExpandedState ? 260f : 0f), 60f, hasExpandedState ? 340f : 260f);
                using var stateBrowserScrollView = new NestedScrollViewScope(ref _stateBrowserScrollPosition, GUILayout.Height(listHeight));
                foreach (LayerMachineEntry item in machines)
                {
                    if (item.machine == null) continue;
                    EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
                    GUILayout.Label(new GUIContent(isSearching ? item.path : item.machine.name,
                        "Sub-state machine: " + item.path), EditorStyles.boldLabel, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
                    GUILayout.Label(item.machine.states.Length + " states", EditorStyles.miniLabel, GUILayout.Width(60));
                    if (GUILayout.Button("Open", EditorStyles.miniButton, GUILayout.Width(45))) navigateTo = item.machine;
                    EditorGUILayout.EndHorizontal();
                }

                foreach (LayerStateEntry item in states)
                {
                    AnimatorState state = item.state;
                    if (state == null || item.owner == null) continue;
                    Color originalBg = GUI.backgroundColor;
                    Color originalColor = GUI.color;
                    if (_selectedState == state) GUI.backgroundColor = new Color(0.4f, 0.7f, 1f, 0.7f);
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    GUI.backgroundColor = originalBg;
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button(_selectedState == state ? "▼" : "►", EditorStyles.miniButton, GUILayout.Width(24)))
                    {
                        if (_selectedState == state) clearStateSelection = true;
                        else stateToSelect = state;
                    }
                    if (GUILayout.Button(new GUIContent(state.name, "Expand or collapse state: " + item.path + "/" + state.name),
                        EditorStyles.label, GUILayout.MinWidth(50), GUILayout.ExpandWidth(true)))
                    {
                        if (_selectedState == state) clearStateSelection = true;
                        else stateToSelect = state;
                    }

                    EditorGUI.BeginChangeCheck();
                    Motion rowMotion = (Motion)EditorGUILayout.ObjectField(state.motion, typeof(Motion), false,
                        GUILayout.MinWidth(65), GUILayout.ExpandWidth(true));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(state, "Change State Motion");
                        state.motion = rowMotion;
                        EditorUtility.SetDirty(state);
                        _hasParamCache = false;
                        _parameterUsageScannedName = null;
                        _setupChecksStale = true;
                        InvalidateLayerSummaries();
                    }

                    if (StateHasWDWarning(state))
                    {
                        GUI.color = new Color(1f, 0.8f, 0.1f);
                        GUILayout.Label(new GUIContent("!", "This Direct Blend Tree state has Write Defaults off."), GUILayout.Width(12));
                        GUI.color = originalColor;
                    }
                    bool stateParameterWarning;
                    if (!_layerStateParameterWarnings.TryGetValue(state, out stateParameterWarning))
                        stateParameterWarning = nonFloatParams != null && StateHasParamWarning(state, nonFloatParams);
                    if (stateParameterWarning)
                    {
                        GUI.color = new Color(1f, 0.4f, 0.4f);
                        GUILayout.Label(new GUIContent("!", "A Blend Tree, Motion Time, or speed multiplier uses a parameter that is not a Float."), GUILayout.Width(12));
                        GUI.color = originalColor;
                    }
                    if (item.owner.defaultState == state)
                    {
                        GUI.color = new Color(1f, 0.65f, 0.25f);
                        GUILayout.Label("Default", EditorStyles.miniBoldLabel, GUILayout.Width(72));
                        GUI.color = originalColor;
                    }
                    else if (GUILayout.Button(new GUIContent("Set Default", "Set the default state in " + item.owner.name),
                        EditorStyles.miniButton, GUILayout.Width(72)))
                    {
                        newDefaultState = state;
                        defaultOwner = item.owner;
                    }
                    if (GUILayout.Button(new GUIContent("Show", "Open this state in the Animator and Inspector."),
                        EditorStyles.miniButton, GUILayout.Width(43)))
                    {
                        EditorApplication.delayCall += () => ShowLayerAnimatorObject(controller, item.owner, state, state);
                        stateToSelect = state;
                    }
                    GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                    if (GUILayout.Button(new GUIContent("X", "Delete this state. Its animation asset is kept. Undo is available."),
                        EditorStyles.miniButton, GUILayout.Width(22)))
                    {
                        stateToDelete = state;
                        deleteOwner = item.owner;
                    }
                    GUI.backgroundColor = originalBg;
                    EditorGUILayout.EndHorizontal();
                    if (_selectedState == state)
                    {
                        GUILayout.Space(4);
                        if (isSearching)
                        {
                            EditorGUILayout.BeginHorizontal();
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button("Open Folder", EditorStyles.miniButton, GUILayout.Width(85)))
                            {
                                navigateTo = item.owner;
                                stateToSelect = state;
                            }
                            EditorGUILayout.EndHorizontal();
                        }
                        DrawStateEditor(controller, item.owner, state);
                    }
                    EditorGUILayout.EndVertical();
                    GUI.color = originalColor;
                }
                stateBrowserScrollView.Dispose();
            }

            // Apply changes after drawing to keep the IMGUI layout stable for this event.
            if (newDefaultState != null && defaultOwner != null)
            {
                Undo.RecordObject(defaultOwner, "Set Default State");
                defaultOwner.defaultState = newDefaultState;
                EditorUtility.SetDirty(defaultOwner);
                _setupChecksStale = true;
                InvalidateLayerSummaries();
            }
            if (stateToDelete != null && deleteOwner != null)
            {
                Undo.RecordObject(deleteOwner, "Delete State");
                deleteOwner.RemoveState(stateToDelete);
                EditorUtility.SetDirty(deleteOwner);
                EditorUtility.SetDirty(controller);
                if (_selectedState == stateToDelete) clearStateSelection = true;
                if (stateToSelect == stateToDelete) stateToSelect = null;
                _hasParamCache = false;
                _setupChecksStale = true;
                InvalidateLayerSummaries();
                RefreshAvailableTrees();
                Repaint();
            }
            if (navigateTo != null)
            {
                var nextPath = new List<AnimatorStateMachine>();
                if (FindLayerStateMachinePath(sm, navigateTo, nextPath, new HashSet<AnimatorStateMachine>()))
                {
                    _stateMachineNavigation.Clear();
                    _stateMachineNavigation.AddRange(nextPath);
                    _stateSearchQuery = "";
                    _stateBrowserScrollPosition = Vector2.zero;
                    clearStateSelection = true;
                    GUI.FocusControl(null);
                    Repaint();
                }
            }
            if (clearStateSelection || stateToSelect != null)
            {
                _selectedState = stateToSelect;
                _selectedTransition = null;
                _selectedIncomingTransition = null;
                Repaint();
            }
            if (createEmptyState || motionsToAdd != null)
            {
                Undo.IncrementCurrentGroup();
                int undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(createEmptyState ? "Add Empty State" : "Add Motion States");
                Undo.RegisterCompleteObjectUndo(currentMachine, "Add States");
                AnimatorState newState = null;
                if (createEmptyState) motionsToAdd = new List<Motion> { null };
                foreach (Motion motion in motionsToAdd)
                {
                    float nextY = 80f;
                    foreach (var child in currentMachine.states) nextY = Mathf.Max(nextY, child.position.y + 70f);
                    foreach (var child in currentMachine.stateMachines) nextY = Mathf.Max(nextY, child.position.y + 70f);
                    // AddState supplies unique names and records creation for Undo.
                    newState = currentMachine.AddState(motion != null ? motion.name : "New State", new Vector3(300f, nextY, 0f));
                    Undo.RecordObject(newState, "Set State Motion");
                    newState.motion = motion;
                    EditorUtility.SetDirty(newState);
                }
                EditorUtility.SetDirty(currentMachine);
                EditorUtility.SetDirty(controller);
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(undoGroup);
                _selectedState = newState;
                _selectedTransition = null;
                _selectedIncomingTransition = null;
                _stateSearchQuery = "";
                _stateBrowserScrollPosition = new Vector2(0, float.MaxValue);
                _layerMotionToAdd = null;
                _hasParamCache = false;
                _setupChecksStale = true;
                InvalidateLayerSummaries();
                GUI.FocusControl(null);
                Repaint();
            }
        }

        private void ValidateStateMachineNavigation(AnimatorStateMachine root)
        {
            if (_stateMachineNavigation.Count == 0 || _stateMachineNavigation[0] != root)
            {
                _stateMachineNavigation.Clear();
                _stateMachineNavigation.Add(root);
                _selectedState = null;
                _selectedTransition = null;
                _selectedIncomingTransition = null;
                _stateBrowserScrollPosition = Vector2.zero;
                return;
            }
            var visited = new HashSet<AnimatorStateMachine> { root };
            for (int i = 1; i < _stateMachineNavigation.Count; i++)
            {
                AnimatorStateMachine item = _stateMachineNavigation[i];
                bool attached = false;
                if (item != null && visited.Add(item))
                {
                    foreach (var child in _stateMachineNavigation[i - 1].stateMachines)
                    {
                        if (child.stateMachine == item) { attached = true; break; }
                    }
                }
                if (!attached)
                {
                    _stateMachineNavigation.RemoveRange(i, _stateMachineNavigation.Count - i);
                    _selectedState = null;
                    _selectedTransition = null;
                    _selectedIncomingTransition = null;
                    _stateBrowserScrollPosition = Vector2.zero;
                    break;
                }
            }
        }

        private static bool FindLayerStateMachinePath(AnimatorStateMachine current, AnimatorStateMachine target,
            List<AnimatorStateMachine> path, HashSet<AnimatorStateMachine> visited)
        {
            if (current == null || !visited.Add(current)) return false;
            path.Add(current);
            if (current == target) return true;
            foreach (var child in current.stateMachines)
            {
                if (FindLayerStateMachinePath(child.stateMachine, target, path, visited)) return true;
            }
            path.RemoveAt(path.Count - 1);
            return false;
        }

        private static void CollectLayerStateSearch(AnimatorStateMachine machine, string path, string query,
            List<LayerStateEntry> states, List<LayerMachineEntry> machines, HashSet<AnimatorStateMachine> visited)
        {
            if (machine == null || !visited.Add(machine)) return;
            bool pathMatches = path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            foreach (var child in machine.states)
            {
                AnimatorState state = child.state;
                if (state == null) continue;
                if (pathMatches || state.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    LayerMotionMatchesSearch(state.motion, query, new HashSet<Motion>()))
                    states.Add(new LayerStateEntry { owner = machine, state = state, path = path });
            }
            foreach (var child in machine.stateMachines)
            {
                AnimatorStateMachine nested = child.stateMachine;
                if (nested == null || visited.Contains(nested)) continue;
                string nestedPath = path + "/" + nested.name;
                if (nestedPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    machines.Add(new LayerMachineEntry { machine = nested, path = nestedPath });
                CollectLayerStateSearch(nested, nestedPath, query, states, machines, visited);
            }
        }

        private static bool LayerMotionMatchesSearch(Motion motion, string query, HashSet<Motion> visited)
        {
            if (motion == null || !visited.Add(motion)) return false;
            if (motion.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            BlendTree tree = motion as BlendTree;
            if (tree == null) return false;
            foreach (ChildMotion child in tree.children)
            {
                if (LayerMotionMatchesSearch(child.motion, query, visited)) return true;
            }
            return false;
        }

        private void ShowLayerAnimatorObject(AnimatorController controller, AnimatorStateMachine machine,
            AnimatorState state, UnityEngine.Object target)
        {
            if (this == null || controller == null || machine == null || target == null || controller != GetAnimatorController()) return;
            var layers = controller.layers;
            int layer = _selectedLayerIndex;
            if (layer < 0 || layer >= layers.Length) return;
            var root = GetParameterUsageLayerRoot(controller, layer);
            if (!FindLayerStateMachinePath(root, machine, new List<AnimatorStateMachine>(), new HashSet<AnimatorStateMachine>())) return;
            var location = new ParameterUsageEntry
            {
                controller = controller, layer = layer, layerName = layers[layer].name,
                layerRoot = root, machine = machine, state = state, target = target
            };
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
            OpenParameterUsageAnimator(location, layer, ++_parameterUsageAnimatorNavigation);
        }

        private void DrawStateEditor(AnimatorController controller, AnimatorStateMachine sm, AnimatorState state)
        {
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(42));
            GUI.SetNextControlName("StateName_" + state.GetInstanceID());
            string newName = EditorGUILayout.DelayedTextField(state.name, GUILayout.MinWidth(60));

            float defaultLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 45;
            float newSpeed = EditorGUILayout.FloatField("Speed", state.speed, GUILayout.Width(100));
            EditorGUIUtility.labelWidth = defaultLabelWidth;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandWidth(true));
            bool newSpeedParamActive = EditorGUILayout.ToggleLeft(new GUIContent("Speed ×", "Use a Float parameter as the speed multiplier."), state.speedParameterActive, GUILayout.Width(78));
            string newSpeedParam = state.speedParameter;
            using (new EditorGUI.DisabledScope(!newSpeedParamActive))
                newSpeedParam = DrawFlexibleParameterComboBox("Spd_" + state.GetInstanceID(), newSpeedParam);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(8);
            EditorGUILayout.BeginHorizontal(GUILayout.ExpandWidth(true));
            bool newTimeParamActive = EditorGUILayout.ToggleLeft(new GUIContent("Motion Time", "Use a Float parameter to control normalized motion time."), state.timeParameterActive, GUILayout.Width(100));
            string newTimeParam = state.timeParameter;
            using (new EditorGUI.DisabledScope(!newTimeParamActive))
                newTimeParam = DrawFlexibleParameterComboBox("Time_" + state.GetInstanceID(), newTimeParam);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Cycle Offset", GUILayout.Width(84));
            bool newCycleOffsetParamActive = state.cycleOffsetParameterActive;
            float newCycleOffset = state.cycleOffset;
            string newCycleOffsetParam = state.cycleOffsetParameter;
            if (newCycleOffsetParamActive) newCycleOffsetParam = DrawFlexibleParameterComboBox("Cyc_" + state.GetInstanceID(), newCycleOffsetParam);
            else newCycleOffset = EditorGUILayout.FloatField(newCycleOffset);
            newCycleOffsetParamActive = EditorGUILayout.ToggleLeft("Parameter", newCycleOffsetParamActive, GUILayout.Width(85));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Options:", GUILayout.Width(84));

            bool newMirrorParamActive = state.mirrorParameterActive;
            bool newMirror = state.mirror;
            string newMirrorParam = state.mirrorParameter;

            if (newMirrorParamActive)
            {
                newMirrorParam = DrawFlexibleParameterComboBox("Mir_" + state.GetInstanceID(), newMirrorParam);
            }
            else
            {
                newMirror = EditorGUILayout.ToggleLeft("Mirror", newMirror, GUILayout.Width(60));
            }

            bool isDirectBlendTree = state.motion is BlendTree bt && bt.blendType == BlendTreeType.Direct;
            bool clickedWD = EditorGUILayout.ToggleLeft("Write Defaults", state.writeDefaultValues, GUILayout.Width(105));
            bool newWD = state.writeDefaultValues;

            if (clickedWD != state.writeDefaultValues)
            {
                if (!clickedWD && isDirectBlendTree)
                {
                    bool turnOff = EditorUtility.DisplayDialog(
                        "Warning: Direct BlendTree Requires Write Defaults",
                        $"The state '{state.name}' is using a Direct Blend Tree.\n\nDisabling 'Write Defaults' on Direct BlendTrees will break weight blending and animation resets in VRChat.\n\nAre you sure you want to turn Write Defaults OFF?",
                        "Turn Off Anyway",
                        "Keep ON"
                    );

                    newWD = turnOff ? false : true;
                }
                else
                {
                    newWD = clickedWD;
                }
            }

            bool newFootIK = EditorGUILayout.ToggleLeft("Foot IK", state.iKOnFeet, GUILayout.ExpandWidth(true));

            newMirrorParamActive = EditorGUILayout.ToggleLeft("Parameter", newMirrorParamActive, GUILayout.Width(85));
            EditorGUILayout.EndHorizontal();

            if (isDirectBlendTree && !newWD)
            {
                EditorGUILayout.HelpBox("⚠️ Warning: Direct Blend Trees require Write Defaults to be ON to function correctly in VRChat.", MessageType.Warning);
            }

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(state, "Edit State");
                if (!string.IsNullOrWhiteSpace(newName)) state.name = newName;
                state.speed = newSpeed;
                state.speedParameterActive = newSpeedParamActive;
                state.speedParameter = newSpeedParam;
                state.timeParameterActive = newTimeParamActive;
                state.timeParameter = newTimeParam;
                state.mirror = newMirror;
                state.mirrorParameterActive = newMirrorParamActive;
                state.mirrorParameter = newMirrorParam;
                state.cycleOffset = newCycleOffset;
                state.cycleOffsetParameterActive = newCycleOffsetParamActive;
                state.cycleOffsetParameter = newCycleOffsetParam;
                state.iKOnFeet = newFootIK;
                state.writeDefaultValues = newWD;
                EditorUtility.SetDirty(state);
            }

            GUILayout.Space(6);
            GuiLine(1);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"Outgoing Transitions ({state.transitions.Length})", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (HubDropdownButton("+ Add Transition", GUILayout.Width(120)))
            {
                HubDropdownMenu menu = new HubDropdownMenu(this);
                foreach (var target in sm.states)
                {
                    if (target.state == null || target.state == state) continue;
                    menu.AddItem(new GUIContent(target.state.name), false, () => {
                        Undo.RecordObject(state, "Add Transition");
                        state.AddTransition(target.state);
                        EditorUtility.SetDirty(state);
                        InvalidateLayerSummaries();
                    });
                }
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Exit"), false, () => {
                    Undo.RecordObject(state, "Add Exit Transition");
                    state.AddExitTransition();
                    EditorUtility.SetDirty(state);
                    InvalidateLayerSummaries();
                });
                menu.ShowAsContext();
            }
            EditorGUILayout.EndHorizontal();

            AnimatorStateTransition transitionToDelete = null;

            foreach (var trans in state.transitions)
            {
                if (trans == null) continue;
                string targetName = trans.isExit ? "Exit" : trans.destinationState != null ? trans.destinationState.name :
                    trans.destinationStateMachine != null ? trans.destinationStateMachine.name : "Missing destination";

                Color origBg = GUI.backgroundColor;
                if (_selectedTransition == trans) GUI.backgroundColor = new Color(0.4f, 0.7f, 1f, 0.7f);

                EditorGUILayout.BeginHorizontal("box");
                if (GUILayout.Button(_selectedTransition == trans ? "▼" : "►", EditorStyles.miniButton, GUILayout.Width(25)))
                {
                    _selectedTransition = (_selectedTransition == trans) ? null : trans;
                    _selectedIncomingTransition = null;
                }

                GUILayout.Label(new GUIContent($"To → {targetName} ({trans.conditions.Length})", "Destination and condition count."), GUILayout.MinWidth(0));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Show", EditorStyles.miniButton, GUILayout.Width(43)))
                    EditorApplication.delayCall += () => ShowLayerAnimatorObject(controller, sm, state, trans);

                GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                if (GUILayout.Button("X", GUILayout.Width(22))) transitionToDelete = trans;
                GUI.backgroundColor = origBg;
                EditorGUILayout.EndHorizontal();

                if (_selectedTransition == trans)
                {
                    DrawTransitionEditor(controller, trans, "Outgoing");
                }
            }

            if (transitionToDelete != null)
            {
                Undo.RecordObject(state, "Delete Transition");
                if (_selectedTransition == transitionToDelete) _selectedTransition = null;
                state.RemoveTransition(transitionToDelete);
                EditorUtility.SetDirty(state);
                InvalidateLayerSummaries();
            }

            GUILayout.Space(6);
            GuiLine(1);

            var incoming = GetIncomingTransitions(_layerSelectionStateMachine != null ? _layerSelectionStateMachine : sm, state);

            EditorGUILayout.BeginHorizontal();
            _showIncomingTransitions = EditorGUILayout.Foldout(_showIncomingTransitions, $"Incoming Transitions ({incoming.Count})", true, EditorStyles.foldoutHeader);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("+ From AnyState", GUILayout.Width(120)))
            {
                Undo.RecordObject(sm, "Add AnyState Transition");
                AnimatorStateTransition newTrans = sm.AddAnyStateTransition(state);
                EditorUtility.SetDirty(sm);
                InvalidateLayerSummaries();

                _showIncomingTransitions = true;
                _selectedIncomingTransition = newTrans;
                _selectedTransition = null;
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();

            bool hasIncomingToDelete = false;
            IncomingTransitionInfo incomingToDelete = default;

            if (_showIncomingTransitions)
            {
                if (incoming.Count == 0)
                {
                    EditorGUILayout.LabelField("  No incoming transitions to this state.", EditorStyles.miniLabel);
                }
                else
                {
                    foreach (var inc in incoming)
                    {
                        if (inc.transition == null || inc.sourceMachine == null) continue;
                        Color origBg = GUI.backgroundColor;
                        if (_selectedIncomingTransition == inc.transition) GUI.backgroundColor = new Color(0.4f, 0.7f, 1f, 0.7f);

                        EditorGUILayout.BeginHorizontal("box");
                        if (GUILayout.Button(_selectedIncomingTransition == inc.transition ? "▼" : "►", EditorStyles.miniButton, GUILayout.Width(25)))
                        {
                            _selectedIncomingTransition = (_selectedIncomingTransition == inc.transition) ? null : inc.transition;
                            _selectedTransition = null;
                        }

                        GUILayout.Label(new GUIContent("From ← " + inc.sourceName, inc.sourceName), GUILayout.MinWidth(0));
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Show", EditorStyles.miniButton, GUILayout.Width(43)))
                            EditorApplication.delayCall += () => ShowLayerAnimatorObject(controller, inc.sourceMachine, inc.sourceState, inc.transition);

                        if (inc.sourceState != null)
                        {
                            if (GUILayout.Button("Select Source", EditorStyles.miniButton, GUILayout.Width(85)))
                            {
                                GUI.FocusControl(null);
                                var layerRoot = _layerSelectionStateMachine;
                                EditorApplication.delayCall += () =>
                                {
                                    if (this == null || GetAnimatorController() != controller || _layerSelectionStateMachine != layerRoot || inc.sourceState == null) return;
                                    var path = new List<AnimatorStateMachine>();
                                    if (!FindLayerStateMachinePath(layerRoot, inc.sourceMachine, path, new HashSet<AnimatorStateMachine>())) return;
                                    _stateMachineNavigation.Clear();
                                    _stateMachineNavigation.AddRange(path);
                                    _stateSearchQuery = "";
                                    _stateBrowserScrollPosition = Vector2.zero;
                                    _selectedState = inc.sourceState;
                                    _selectedTransition = inc.transition;
                                    _selectedIncomingTransition = null;
                                    Repaint();
                                };
                            }
                        }

                        GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                        if (GUILayout.Button("X", GUILayout.Width(22)))
                        {
                            hasIncomingToDelete = true;
                            incomingToDelete = inc;
                        }
                        GUI.backgroundColor = origBg;

                        EditorGUILayout.EndHorizontal();

                        if (_selectedIncomingTransition == inc.transition)
                        {
                            DrawTransitionEditor(controller, inc.transition, "Incoming");
                        }
                    }
                }
            }

            if (hasIncomingToDelete)
            {
                if (_selectedIncomingTransition == incomingToDelete.transition) _selectedIncomingTransition = null;

                if (incomingToDelete.sourceState == null)
                {
                    var owner = incomingToDelete.sourceMachine;
                    Undo.RecordObject(owner, "Delete AnyState Transition");

                    var list = new List<AnimatorStateTransition>(owner.anyStateTransitions);
                    if (list.Remove(incomingToDelete.transition))
                    {
                        owner.anyStateTransitions = list.ToArray();
                        Undo.DestroyObjectImmediate(incomingToDelete.transition);
                        EditorUtility.SetDirty(owner);
                    }
                }
                else
                {
                    Undo.RecordObject(incomingToDelete.sourceState, "Delete Incoming Transition");
                    incomingToDelete.sourceState.RemoveTransition(incomingToDelete.transition);
                    EditorUtility.SetDirty(incomingToDelete.sourceState);
                }
                InvalidateLayerSummaries();
            }

            GUILayout.Space(6);
            GuiLine(1);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("State Behaviours (" + state.behaviours.Length + ")", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (HubDropdownButton("+ Add Behaviour", GUILayout.Width(120)))
            {
                HubDropdownMenu menu = new HubDropdownMenu(this);

                string[] allowedVrcBehaviours = new string[]
                {
                    "VRCAnimatorLayerControl",
                    "VRCAnimatorLocomotionControl",
                    "VRCAnimatorPlayAudio",
                    "VRCAnimatorTemporaryPoseSpace",
                    "VRCAnimatorTrackingControl",
                    "VRCAvatarParameterDriver",
                    "VRCPlayableLayerControl"
                };

                var types = TypeCache.GetTypesDerivedFrom<StateMachineBehaviour>();
                foreach (var t in types)
                {
                    if (!t.IsAbstract && !t.IsGenericType && System.Array.Exists(allowedVrcBehaviours, name => name == t.Name))
                    {
                        menu.AddItem(new GUIContent(t.Name), false, () => {
                            if (state == null) return;
                            Undo.RecordObject(state, "Add Behaviour");
                            _expandedLayerBehaviour = state.AddStateMachineBehaviour(t);
                            Undo.RegisterCreatedObjectUndo(_expandedLayerBehaviour, "Add Behaviour");
                            EditorUtility.SetDirty(state);
                            InvalidateLayerSummaries();
                        });
                    }
                }
                menu.ShowAsContext();
            }
            EditorGUILayout.EndHorizontal();

            StateMachineBehaviour behaviourToDelete = null;

            foreach (var behaviour in state.behaviours)
            {
                if (behaviour == null) continue;
                Color behaviourBackground = GUI.backgroundColor;
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button((_expandedLayerBehaviour == behaviour ? "▼ " : "► ") + behaviour.GetType().Name,
                    EditorStyles.label, GUILayout.ExpandWidth(true)))
                {
                    _expandedLayerBehaviour = _expandedLayerBehaviour == behaviour ? null : behaviour;
                    if (_cachedBehaviourEditor != null) DestroyImmediate(_cachedBehaviourEditor);
                    _cachedBehaviourEditor = null;
                    Repaint();
                }
                GUILayout.FlexibleSpace();
                GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                if (GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(22))) behaviourToDelete = behaviour;
                GUI.backgroundColor = behaviourBackground;
                EditorGUILayout.EndHorizontal();

                if (_expandedLayerBehaviour == behaviour)
                {
                    Editor.CreateCachedEditor(behaviour, null, ref _cachedBehaviourEditor);
                    if (_cachedBehaviourEditor != null) _cachedBehaviourEditor.OnInspectorGUI();
                }
                EditorGUILayout.EndVertical();
            }

            if (behaviourToDelete != null)
            {
                if (_expandedLayerBehaviour == behaviourToDelete)
                {
                    _expandedLayerBehaviour = null;
                    if (_cachedBehaviourEditor != null) DestroyImmediate(_cachedBehaviourEditor);
                    _cachedBehaviourEditor = null;
                }
                Undo.RecordObject(state, "Delete Behaviour");
                Undo.DestroyObjectImmediate(behaviourToDelete);
                EditorUtility.SetDirty(state);
                InvalidateLayerSummaries();
            }
        }

        private struct IncomingTransitionInfo
        {
            public string sourceName;
            public AnimatorStateMachine sourceMachine;
            public AnimatorState sourceState;
            public AnimatorStateTransition transition;
        }

        private List<IncomingTransitionInfo> GetIncomingTransitions(AnimatorStateMachine sm, AnimatorState targetState)
        {
            if (_incomingTransitionRoot == sm && _incomingTransitionTarget == targetState &&
                (Event.current.type != EventType.Layout || EditorApplication.timeSinceStartup < _nextIncomingTransitionRefresh))
                return _cachedIncomingTransitions;
            _incomingTransitionRoot = sm;
            _incomingTransitionTarget = targetState;
            _nextIncomingTransitionRefresh = EditorApplication.timeSinceStartup + 1;
            _cachedIncomingTransitions.Clear();
            var visited = new HashSet<AnimatorStateMachine>();
            void Collect(AnimatorStateMachine machine, string path)
            {
                if (machine == null || targetState == null || !visited.Add(machine)) return;
                foreach (var trans in machine.anyStateTransitions)
                    if (trans != null && trans.destinationState == targetState)
                        _cachedIncomingTransitions.Add(new IncomingTransitionInfo
                        { sourceName = path + " / Any State", sourceMachine = machine, transition = trans });
                foreach (var child in machine.states)
                    if (child.state != null)
                        foreach (var trans in child.state.transitions)
                            if (trans != null && trans.destinationState == targetState)
                                _cachedIncomingTransitions.Add(new IncomingTransitionInfo
                                { sourceName = path + " / " + child.state.name, sourceMachine = machine, sourceState = child.state, transition = trans });
                foreach (var child in machine.stateMachines)
                    if (child.stateMachine != null) Collect(child.stateMachine, path + " / " + child.stateMachine.name);
            }
            Collect(sm, sm != null ? sm.name : "");
            return _cachedIncomingTransitions;
        }

        private void DrawTransitionEditor(AnimatorController controller, AnimatorStateTransition trans, string context)
        {
            string dropdownId = "Transition_" + trans.GetInstanceID() + "_" + context;
            EditorGUILayout.BeginVertical("helpbox");

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.BeginHorizontal();
            bool hasExitTime = EditorGUILayout.ToggleLeft("Exit Time", trans.hasExitTime, GUILayout.Width(78));
            float exitTime;
            using (new EditorGUI.DisabledScope(!hasExitTime))
                exitTime = EditorGUILayout.FloatField(trans.exitTime, GUILayout.Width(55));
            GUILayout.Label("Duration", GUILayout.Width(55));
            float duration = EditorGUILayout.FloatField(trans.duration, GUILayout.MinWidth(45));
            bool hasFixedDuration = EditorGUILayout.ToggleLeft(new GUIContent("Seconds", "Use seconds for duration; otherwise use normalized state time."), trans.hasFixedDuration, GUILayout.Width(78));
            bool mute = EditorGUILayout.ToggleLeft("Mute", trans.mute, GUILayout.Width(55));
            bool solo = EditorGUILayout.ToggleLeft("Solo", trans.solo, GUILayout.Width(50));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Offset", GUILayout.Width(42));
            float offset = EditorGUILayout.FloatField(trans.offset, GUILayout.Width(55));
            GUILayout.Label("Interruption", GUILayout.Width(76));
            var interruption = (TransitionInterruptionSource)HubEnumPopup(dropdownId + "_Interruption",
                null, trans.interruptionSource, GUILayout.MinWidth(70));
            bool ordered = EditorGUILayout.ToggleLeft("Ordered", trans.orderedInterruption, GUILayout.Width(76));
            bool canSelf = EditorGUILayout.ToggleLeft(new GUIContent("To Self", "Allow an Any State transition to enter the state that is already playing."), trans.canTransitionToSelf, GUILayout.Width(70));
            EditorGUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(trans, "Edit Transition");
                trans.hasExitTime = hasExitTime;
                trans.exitTime = Mathf.Max(0f, exitTime);
                trans.hasFixedDuration = hasFixedDuration;
                trans.duration = Mathf.Max(0f, duration);
                trans.offset = offset;
                trans.interruptionSource = interruption;
                trans.orderedInterruption = ordered;
                trans.canTransitionToSelf = canSelf;
                trans.mute = mute;
                trans.solo = solo;
                EditorUtility.SetDirty(trans);
            }

            AnimatorCondition[] conditions = trans.conditions;
            var parameters = controller.parameters;
            var parameterTypes = new Dictionary<string, AnimatorControllerParameterType>(StringComparer.Ordinal);
            foreach (var parameter in parameters) parameterTypes[parameter.name] = parameter.type;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Conditions (" + conditions.Length + ")", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (HubDropdownButtonWithId(dropdownId + "_AddCondition", new GUIContent("+ Condition ▾"), GUI.skin.button, GUILayout.Width(110)))
            {
                var options = new HubDropdownMenu(this);
                foreach (var parameter in parameters)
                {
                    string parameterName = parameter.name;
                    options.AddItem(new GUIContent(parameterName), false, () =>
                    {
                        if (controller == null || trans == null) return;
                        var chosen = Array.Find(controller.parameters, item => item.name == parameterName);
                        if (chosen == null) return;
                        var mode = chosen.type == AnimatorControllerParameterType.Bool || chosen.type == AnimatorControllerParameterType.Trigger
                            ? AnimatorConditionMode.If : chosen.type == AnimatorControllerParameterType.Int
                            ? AnimatorConditionMode.Equals : AnimatorConditionMode.Greater;
                        Undo.RecordObject(trans, "Add Condition");
                        trans.AddCondition(mode, 0f, parameterName);
                        EditorUtility.SetDirty(trans);
                        _parameterUsageScannedName = null;
                        Repaint();
                    });
                }
                if (parameters.Length == 0) options.AddDisabledItem(new GUIContent("No Animator parameters"));
                options.ShowAsContext();
            }
            EditorGUILayout.EndHorizontal();

            int conditionToRemove = -1;

            for (int i = 0; i < conditions.Length; i++)
            {
                EditorGUILayout.BeginHorizontal();

                EditorGUI.BeginChangeCheck();

                string selectedParamName = DrawFlexibleParameterComboBox(dropdownId + "_Parameter_" + i, conditions[i].parameter);

                bool parameterExists = parameterTypes.TryGetValue(selectedParamName ?? "", out var paramType);
                if (!parameterExists) paramType = AnimatorControllerParameterType.Float;

                AnimatorConditionMode[] validModes;
                string[] modeLabels;

                if (paramType == AnimatorControllerParameterType.Trigger)
                {
                    validModes = new[] { AnimatorConditionMode.If };
                    modeLabels = new[] { "Set" };
                }
                else if (paramType == AnimatorControllerParameterType.Bool)
                {
                    validModes = new AnimatorConditionMode[] { AnimatorConditionMode.If, AnimatorConditionMode.IfNot };
                    modeLabels = new string[] { "true", "false" };
                }
                else if (paramType == AnimatorControllerParameterType.Int)
                {
                    validModes = new AnimatorConditionMode[] { AnimatorConditionMode.Greater, AnimatorConditionMode.Less, AnimatorConditionMode.Equals, AnimatorConditionMode.NotEqual };
                    modeLabels = new string[] { "Greater", "Less", "Equals", "NotEqual" };
                }
                else
                {
                    validModes = new AnimatorConditionMode[] { AnimatorConditionMode.Greater, AnimatorConditionMode.Less };
                    modeLabels = new string[] { "Greater", "Less" };
                }

                AnimatorConditionMode currentMode = conditions[i].mode;
                if (selectedParamName != conditions[i].parameter && System.Array.IndexOf(validModes, currentMode) == -1)
                {
                    currentMode = validModes[0];
                }
                if (!parameterExists || System.Array.IndexOf(validModes, currentMode) == -1)
                    GUILayout.Label(new GUIContent("!", !parameterExists ? "This parameter does not exist in the Animator." :
                        "This condition mode does not match the parameter type. Choose a valid comparison."), GUILayout.Width(12));

                int[] modeInts = new int[validModes.Length];
                for (int m = 0; m < validModes.Length; m++) modeInts[m] = (int)validModes[m];

                AnimatorConditionMode newMode = (AnimatorConditionMode)HubIntPopup(dropdownId + "_Mode_" + i,
                    (int)currentMode, modeLabels, modeInts, GUILayout.Width(80));

                float newThreshold = conditions[i].threshold;
                if (paramType == AnimatorControllerParameterType.Int)
                    newThreshold = EditorGUILayout.IntField(Mathf.RoundToInt(newThreshold), GUILayout.Width(60));
                else if (paramType == AnimatorControllerParameterType.Float)
                {
                    newThreshold = EditorGUILayout.FloatField(newThreshold, GUILayout.Width(60));
                }
                else newThreshold = 0f;

                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(trans, "Edit Condition");
                    conditions[i].parameter = selectedParamName;
                    conditions[i].mode = newMode;
                    conditions[i].threshold = newThreshold;
                    trans.conditions = conditions;
                    EditorUtility.SetDirty(trans);
                }

                Color conditionBackground = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                if (GUILayout.Button("-", GUILayout.Width(22))) conditionToRemove = i;
                GUI.backgroundColor = conditionBackground;

                EditorGUILayout.EndHorizontal();
            }

            if (conditionToRemove >= 0)
            {
                Undo.RecordObject(trans, "Remove Condition");
                var list = new List<AnimatorCondition>(conditions);
                list.RemoveAt(conditionToRemove);
                trans.conditions = list.ToArray();
                EditorUtility.SetDirty(trans);
            }

            EditorGUILayout.EndVertical();
        }

        private void ResetMenuControlSelection()
        {
            _selectedMenuOwner = null;
            _selectedMenuControlReference = null;
            _selectedMenuControlIndex = -1;
            ResetMenuDrag();
        }

        private void SelectMenuControl(VRCExpressionsMenu owner, int index)
        {
            ResetMenuControlSelection();
            if (owner == null || owner.controls == null || index < 0 || index >= owner.controls.Count) return;
            _selectedMenuOwner = owner;
            _selectedMenuControlIndex = index;
            _selectedMenuControlReference = owner.controls[index];
        }

        private void MarkMenuChanged(VRCExpressionsMenu menu)
        {
            if (menu != null) EditorUtility.SetDirty(menu);
            _nextMenuSearchRefresh = 0;
            if (_hasSetupScan) _setupChecksStale = true;
            Repaint();
        }

        private static string MenuPlainText(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var name = ReadMenuNameColours(value);
            var text = new System.Text.StringBuilder(name.text);
            for (int i = name.markers.Count - 1; i >= 0; i--)
            {
                var marker = name.markers[i];
                string key = MenuNameTagPattern.Match(marker.raw).Groups["name"].Value.ToLowerInvariant();
                if (key == "br" || key == "space" || key == "nbsp" || key == "page") text.Insert(marker.position, " ");
            }
            return text.ToString();
        }

        private static string MenuControlTypeName(VRCExpressionsMenu.Control.ControlType type)
        {
            switch (type)
            {
                case VRCExpressionsMenu.Control.ControlType.SubMenu: return "Submenu";
                case VRCExpressionsMenu.Control.ControlType.RadialPuppet: return "Radial Puppet";
                case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet: return "Two Axis Puppet";
                case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet: return "Four Axis Puppet";
                default: return type.ToString();
            }
        }

        private static string MenuControlSummary(VRCExpressionsMenu.Control control, bool includeLabel = true)
        {
            if (control == null) return "";
            if (control.type == Control.ControlType.SubMenu)
                return control.subMenu != null ? (includeLabel ? "Submenu: " : "") + control.subMenu.name :
                    (includeLabel ? "No submenu assigned" : "None");
            int axes = control.type == Control.ControlType.RadialPuppet ? 1 :
                control.type == Control.ControlType.TwoAxisPuppet ? 2 : control.type == Control.ControlType.FourAxisPuppet ? 4 : 0;
            if (axes > 0)
            {
                var names = new List<string>();
                for (int i = 0; i < axes; i++)
                {
                    string name = control.subParameters != null && i < control.subParameters.Length && control.subParameters[i] != null ? control.subParameters[i].name : "";
                    names.Add(string.IsNullOrEmpty(name) ? "None" : name);
                }
                return (includeLabel ? (axes == 1 ? "Rotation: " : "Axes: ") : "") + string.Join(", ", names);
            }
            return (includeLabel ? "Parameter: " : "") + (control.parameter != null && !string.IsNullOrEmpty(control.parameter.name) ? control.parameter.name : "None");
        }

        private void NavigateMenu(VRCExpressionsMenu target)
        {
            if (target == null) return;
            CancelMenuDrag();
            var current = CurrentMenu;
            int ancestor = _menuNavigationPath.IndexOf(target);
            if (ancestor >= 0) _menuNavigationPath.RemoveRange(ancestor + 1, _menuNavigationPath.Count - ancestor - 1);
            else if (current != null && current.controls != null && current.controls.Exists(
                control => control != null && control.type == Control.ControlType.SubMenu && control.subMenu == target))
                _menuNavigationPath.Add(target);
            else
            {
                var path = new List<VRCExpressionsMenu>();
                if (!FindMenuEditorPath(_vrcExpressionMenuAsset, target, path, new HashSet<VRCExpressionsMenu>())) return;
                _menuNavigationPath.Clear();
                _menuNavigationPath.AddRange(path);
            }
            _menuSearchQuery = "";
            SetMenuSearchFilter(_menuSearchAll, -1);
            _menuScrollPos = Vector2.zero;
            ResetMenuControlSelection();
            Repaint();
        }

        private static bool FindMenuEditorPath(VRCExpressionsMenu menu, VRCExpressionsMenu target,
            List<VRCExpressionsMenu> path, HashSet<VRCExpressionsMenu> visited)
        {
            if (menu == null || !visited.Add(menu)) return false;
            path.Add(menu);
            if (menu == target) return true;
            if (menu.controls != null)
                foreach (var control in menu.controls)
                    if (control != null && control.type == VRCExpressionsMenu.Control.ControlType.SubMenu &&
                        FindMenuEditorPath(control.subMenu, target, path, visited)) return true;
            path.RemoveAt(path.Count - 1);
            return false;
        }

        private void CollectMenuEditorSearch(VRCExpressionsMenu menu, string path, string query, HashSet<VRCExpressionsMenu> visited)
        {
            if (menu == null || !visited.Add(menu) || menu.controls == null) return;
            for (int i = 0; i < menu.controls.Count; i++)
            {
                var control = menu.controls[i];
                if (control == null) continue;
                string plainName = MenuPlainText(control.name);
                bool matches = path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    plainName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    MenuControlSummary(control, false).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    MenuControlTypeName(control.type).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (control.parameter != null && (control.parameter.name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!matches && control.subParameters != null)
                    foreach (var parameter in control.subParameters)
                        if (parameter != null && (parameter.name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) { matches = true; break; }
                if (matches && (_menuTypeFilter < 0 || (int)control.type == _menuTypeFilter))
                    _menuSearchResults.Add(new MenuControlEntry { owner = menu, control = control, index = i, path = path });
                if (_menuSearchAll && control.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                    CollectMenuEditorSearch(control.subMenu, path + " / " + plainName, query, visited);
            }
        }

        private static void ClearMenuCut()
        {
            _menuCuts.Clear();
        }

        private static bool IsMenuControlCut(VRCExpressionsMenu owner, VRCExpressionsMenu.Control control)
        {
            return _menuCuts.Exists(entry => entry.owner == owner && ReferenceEquals(entry.control, control));
        }

        private bool CanPasteMenuControl(VRCExpressionsMenu target, out string reason)
        {
            reason = "Copy or cut controls first. Click Cut on each control to move several together.";
            int incoming = _menuCuts.Count > 0 ? _menuCuts.Count : _menuClipboard.Count;
            if (target == null || incoming == 0) return false;
            int count = target.controls != null ? target.controls.Count : 0;
            foreach (var cut in _menuCuts)
            {
                if (cut.owner == null || cut.owner.controls == null || cut.control == null || !cut.owner.controls.Contains(cut.control))
                {
                    reason = "A cut control changed or was removed. Cancel Cut and select the controls again.";
                    return false;
                }
                if (cut.owner == target) count--;
                else if (cut.control.type == Control.ControlType.SubMenu && cut.control.subMenu != null &&
                    FindMenuEditorPath(cut.control.subMenu, target, new List<VRCExpressionsMenu>(), new HashSet<VRCExpressionsMenu>()))
                {
                    reason = "Cannot move a submenu control into itself or one of its own submenus.";
                    return false;
                }
            }
            if (count + incoming > VRCExpressionsMenu.MAX_CONTROLS)
            {
                reason = "The whole batch needs " + incoming + " slots; this menu has room for " +
                    Mathf.Max(0, VRCExpressionsMenu.MAX_CONTROLS - count) + ". Reduce the cut batch or choose another menu.";
                return false;
            }
            reason = (_menuCuts.Count > 0 ? "Move " : "Paste ") + incoming + " control(s) here in the order you selected them.";
            return true;
        }

        private void CopyMenuControl(VRCExpressionsMenu owner, int index, bool cut)
        {
            if (owner == null || owner.controls == null || index < 0 || index >= owner.controls.Count || owner.controls[index] == null) return;
            if (cut)
            {
                var control = owner.controls[index];
                int existing = _menuCuts.FindIndex(entry => entry.owner == owner && ReferenceEquals(entry.control, control));
                if (existing >= 0) _menuCuts.RemoveAt(existing);
                else
                {
                    if (_menuCuts.Count == 0) _menuClipboard.Clear();
                    _menuCuts.Add(new MenuCutEntry { owner = owner, control = control });
                }
            }
            else
            {
                ClearMenuCut();
                _menuClipboard.Clear();
                _menuClipboard.Add(CloneMenuControl(owner.controls[index]));
            }
            Repaint();
        }

        private void PasteMenuControl(VRCExpressionsMenu target)
        {
            CancelMenuDrag();
            if (!CanPasteMenuControl(target, out string reason))
            {
                EditorUtility.DisplayDialog("Cannot Paste Controls", reason, "OK");
                return;
            }
            var pasted = new List<VRCExpressionsMenu.Control>();
            var changes = new Dictionary<VRCExpressionsMenu, List<VRCExpressionsMenu.Control>>();
            changes[target] = target.controls != null ? new List<VRCExpressionsMenu.Control>(target.controls) : new List<VRCExpressionsMenu.Control>();
            foreach (var cut in _menuCuts)
            {
                if (!changes.ContainsKey(cut.owner)) changes[cut.owner] = new List<VRCExpressionsMenu.Control>(cut.owner.controls);
                changes[cut.owner].Remove(cut.control);
                pasted.Add(CloneMenuControl(cut.control));
            }
            if (_menuCuts.Count == 0)
                foreach (var control in _menuClipboard) pasted.Add(CloneMenuControl(control));
            changes[target].AddRange(pasted);

            string undoName = _menuCuts.Count > 0 ? "Move Menu Controls" : "Paste Menu Controls";
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            try
            {
                foreach (var change in changes) Undo.RegisterCompleteObjectUndo(change.Key, undoName);
                foreach (var change in changes)
                {
                    change.Key.controls = change.Value;
                    MarkMenuChanged(change.Key);
                }
                Undo.FlushUndoRecordObjects();
                AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(group);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(group);
                // Restoring serialized controls replaces their managed references.
                ClearMenuCut();
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Cannot Paste Controls", "The move was rolled back.\n\n" + exception.Message, "OK");
                return;
            }
            _menuClipboard.Clear();
            foreach (var control in pasted) _menuClipboard.Add(CloneMenuControl(control));
            ClearMenuCut();
            NavigateMenu(target);
            SelectMenuControl(target, target.controls.Count - 1);
            _menuScrollPos = new Vector2(0, float.MaxValue);
            MarkMenuChanged(target);
        }

        private void AddMenuControl(VRCExpressionsMenu menu, VRCExpressionsMenu.Control.ControlType type)
        {
            if (menu == null || (menu.controls != null && menu.controls.Count >= VRCExpressionsMenu.MAX_CONTROLS)) return;
            var control = new VRCExpressionsMenu.Control
            {
                name = "New " + MenuControlTypeName(type),
                type = type,
                value = 1,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "" },
                subParameters = new VRCExpressionsMenu.Control.Parameter[0],
                labels = new VRCExpressionsMenu.Control.Label[0]
            };
            int axes = type == VRCExpressionsMenu.Control.ControlType.RadialPuppet ? 1 :
                type == VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet ? 2 :
                type == VRCExpressionsMenu.Control.ControlType.FourAxisPuppet ? 4 : 0;
            control.subParameters = new VRCExpressionsMenu.Control.Parameter[axes];
            for (int i = 0; i < axes; i++) control.subParameters[i] = new VRCExpressionsMenu.Control.Parameter { name = "" };
            if (axes > 1)
            {
                control.labels = new VRCExpressionsMenu.Control.Label[4];
                string[] names = { "Up", "Right", "Down", "Left" };
                for (int i = 0; i < 4; i++) control.labels[i] = new VRCExpressionsMenu.Control.Label { name = names[i] };
            }
            Undo.RegisterCompleteObjectUndo(menu, "Add Menu Control");
            if (menu.controls == null) menu.controls = new List<VRCExpressionsMenu.Control>();
            menu.controls.Add(control);
            NavigateMenu(menu);
            SelectMenuControl(menu, menu.controls.Count - 1);
            _menuScrollPos = new Vector2(0, float.MaxValue);
            MarkMenuChanged(menu);
        }

        private void SetMenuSearchFilter(bool allMenus, int controlType)
        {
            CancelMenuDrag();
            _menuSearchAll = allMenus;
            _menuTypeFilter = controlType;
            _nextMenuSearchRefresh = 0;
            _menuSearchRoot = null;
            _menuScrollPos = Vector2.zero;
            EditorPrefs.SetBool(PrefMenuSearchAllKey, allMenus);
            EditorPrefs.SetInt(PrefMenuTypeFilterKey, controlType);
            Repaint();
        }

        private void ShowMenuSearchFilter()
        {
            var menu = new HubDropdownMenu(this, true);
            menu.AddItem(new GUIContent("Search In/All Menus"), _menuSearchAll, () => SetMenuSearchFilter(true, _menuTypeFilter));
            menu.AddItem(new GUIContent("Search In/Current Menu"), !_menuSearchAll, () => SetMenuSearchFilter(false, _menuTypeFilter));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Control Type/All Types"), _menuTypeFilter < 0, () => SetMenuSearchFilter(_menuSearchAll, -1));
            foreach (Control.ControlType type in Enum.GetValues(typeof(Control.ControlType)))
            {
                var chosenType = type;
                menu.AddItem(new GUIContent("Control Type/" + MenuControlTypeName(type)), _menuTypeFilter == (int)type,
                    () => SetMenuSearchFilter(_menuSearchAll, (int)chosenType));
            }
            menu.ShowAsContext();
        }

        private VRCExpressionsMenu DrawCompactMenuBreadcrumbs()
        {
            int count = _menuNavigationPath.Count;
            if (count == 0) return null;
            var widths = new float[count];
            float total = 0f;
            for (int i = 0; i < count; i++)
            {
                widths[i] = Mathf.Clamp(EditorStyles.toolbarButton.CalcSize(new GUIContent(_menuNavigationPath[i].name)).x + 8f, 35f, 180f);
                total += widths[i] + (i > 0 ? 19f : 0f);
            }
            float available = Mathf.Max(230f, position.width - 130f);
            int firstVisible = 1;
            if (total > available && count > 2)
            {
                total += 47f;
                while (firstVisible < count - 1 && total > available) total -= widths[firstVisible++] + 19f;
            }
            VRCExpressionsMenu target = null;
            for (int i = 0; i < count; i++)
            {
                if (i > 0 && i < firstVisible) continue;
                if (i > 0) GUILayout.Label(">", EditorStyles.label, GUILayout.Width(15));
                if (i == firstVisible && firstVisible > 1)
                {
                    if (HubDropdownButton(new GUIContent("…", "Open an earlier menu in this path"), EditorStyles.toolbarButton, GUILayout.Width(28)))
                    {
                        var hidden = new HubDropdownMenu(this);
                        for (int j = 1; j < firstVisible; j++)
                        {
                            var ancestor = _menuNavigationPath[j];
                            hidden.AddItem(new GUIContent(j + ": " + ancestor.name.Replace('/', '∕')), false, () => NavigateMenu(ancestor));
                        }
                        hidden.ShowAsContext();
                    }
                    GUILayout.Label(">", EditorStyles.label, GUILayout.Width(15));
                }
                var item = _menuNavigationPath[i];
                if (GUILayout.Button(new GUIContent(item.name, AssetDatabase.GetAssetPath(item)), EditorStyles.toolbarButton, GUILayout.Width(widths[i]))) target = item;
            }
            return target;
        }

        private void EnsureMenuRowStyles()
        {
            bool proSkin = EditorGUIUtility.isProSkin;
            if (_menuControlNameStyle != null && _menuMetadataStyle != null && _menuStylesProSkin == proSkin) return;

            _menuStylesProSkin = proSkin;
            _menuControlNameStyle = new GUIStyle(EditorStyles.label)
            {
                richText = true,
                clipping = TextClipping.Clip
            };
            _menuMetadataStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight,
                clipping = TextClipping.Clip,
                wordWrap = false
            };
        }

        private void DrawMenuEditorSection()
        {
            if (!_showMenuEditor)
            {
                CancelMenuDrag();
                return;
            }
            GUILayout.Space(5);
            EditorGUILayout.LabelField("VRC Menu Editor", EditorStyles.boldLabel);
            if (_menuEditorRoot != _vrcExpressionMenuAsset)
            {
                CancelMenuDrag();
                _menuEditorRoot = _vrcExpressionMenuAsset;
                ResetMenuControlSelection();
                _menuSearchQuery = "";
                _nextMenuSearchRefresh = 0;
            }
            var currentMenu = CurrentMenu;
            if (_isMenuDragging && _menuDragOwner != currentMenu) CancelMenuDrag();
            EditorGUILayout.BeginVertical("box");
            if (currentMenu == null)
            {
                EditorGUILayout.HelpBox("Assign a VRC Menu Root above to edit menus here.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }
            if (_selectedMenuOwner != null && _selectedMenuOwner.controls != null && _selectedMenuControlReference != null)
            {
                _selectedMenuControlIndex = _selectedMenuOwner.controls.IndexOf(_selectedMenuControlReference);
                if (_selectedMenuControlIndex < 0) ResetMenuControlSelection();
            }

            VRCExpressionsMenu navigateTo = null;
            VRCExpressionsMenu navigateFrom = null;
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            navigateTo = DrawCompactMenuBreadcrumbs();
            if (navigateTo != null) GUI.FocusControl(null);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Show Asset", EditorStyles.toolbarButton, GUILayout.Width(80))) { Selection.activeObject = currentMenu; EditorGUIUtility.PingObject(currentMenu); }
            EditorGUILayout.EndHorizontal();

            int count = currentMenu.controls != null ? currentMenu.controls.Count : 0;
            int limit = VRCExpressionsMenu.MAX_CONTROLS;
            bool paste = false;
            EditorGUILayout.BeginHorizontal();
            bool hasFilter = !string.IsNullOrWhiteSpace(_menuSearchQuery) || _menuTypeFilter >= 0;
            string countText = "Controls (" + count + " / " + limit + ")" + (count >= limit ? " · Full" : "");
            if (hasFilter) countText += " · " + _menuSearchResults.Count + " found";
            GUILayout.Label(new GUIContent(countText, count >= limit ? "This menu is full. Open a submenu to add more controls." :
                (limit - count) + " free slots."), EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (_menuCuts.Count > 0 && GUILayout.Button(new GUIContent("Cancel Cut", "Clear all queued cuts."), GUILayout.Width(80))) ClearMenuCut();
            int pasteCount = _menuCuts.Count > 0 ? _menuCuts.Count : _menuClipboard.Count;
            bool canPaste = CanPasteMenuControl(currentMenu, out string pasteReason);
            using (new EditorGUI.DisabledScope(!canPaste))
                if (GUILayout.Button(new GUIContent(pasteCount > 1 ? "Paste (" + pasteCount + ")" : "Paste", pasteReason),
                    GUILayout.Width(pasteCount > 1 ? 76 : 52))) paste = true;
            using (new EditorGUI.DisabledScope(count >= limit))
            {
                if (HubDropdownButton("+ Add Control", GUILayout.Width(112)))
                {
                    var addMenu = new HubDropdownMenu(this);
                    var types = new[] { Control.ControlType.Toggle, Control.ControlType.Button, Control.ControlType.SubMenu,
                        Control.ControlType.RadialPuppet, Control.ControlType.TwoAxisPuppet, Control.ControlType.FourAxisPuppet };
                    foreach (var type in types)
                    {
                        var chosenType = type;
                        addMenu.AddItem(new GUIContent(MenuControlTypeName(type)), false, () => AddMenuControl(currentMenu, chosenType));
                    }
                    addMenu.ShowAsContext();
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _menuSearchQuery = EditorGUILayout.TextField(new GUIContent("Find control or parameter", "Search names, submenu assets, control types and parameters."), _menuSearchQuery, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck()) _menuScrollPos = Vector2.zero;
            if (HubDropdownButtonWithId("MenuSearchFilter", new GUIContent(_menuTypeFilter >= 0 || !_menuSearchAll ? "Filter* ▾" : "Filter ▾",
                (_menuSearchAll ? "All menus" : "Current menu") + " / " + (_menuTypeFilter < 0 ? "All control types" : MenuControlTypeName((Control.ControlType)_menuTypeFilter))), GUI.skin.button, GUILayout.Width(72))) ShowMenuSearchFilter();
            if (GUILayout.Button(new GUIContent("Clear", "Clear the search and control-type filter."), GUILayout.Width(45)))
            { _menuSearchQuery = ""; SetMenuSearchFilter(_menuSearchAll, -1); GUI.FocusControl(null); }
            EditorGUILayout.EndHorizontal();

            string query = _menuSearchQuery.Trim();
            bool searching = query.Length > 0 || _menuTypeFilter >= 0;
            if (!searching && _selectedMenuOwner != null && _selectedMenuOwner != currentMenu) ResetMenuControlSelection();
            var entries = new List<MenuControlEntry>();
            if (searching)
            {
                CancelMenuDrag();
                var searchRoot = _menuSearchAll ? _vrcExpressionMenuAsset : currentMenu;
                if (_menuSearchRoot != searchRoot || _menuSearchText != query || _menuSearchCachedType != _menuTypeFilter ||
                    (Event.current.type == EventType.Layout && EditorApplication.timeSinceStartup >= _nextMenuSearchRefresh))
                {
                    _menuSearchResults.Clear();
                    CollectMenuEditorSearch(searchRoot, searchRoot.name, query, new HashSet<VRCExpressionsMenu>());
                    _menuSearchRoot = searchRoot;
                    _menuSearchText = query;
                    _menuSearchCachedType = _menuTypeFilter;
                    _nextMenuSearchRefresh = EditorApplication.timeSinceStartup + 1;
                }
                entries = _menuSearchResults;
            }
            else if (currentMenu.controls != null)
                for (int i = 0; i < currentMenu.controls.Count; i++) entries.Add(new MenuControlEntry { owner = currentMenu, control = currentMenu.controls[i], index = i, path = currentMenu.name });

            Event e = Event.current;
            Vector2 pointer = e.mousePosition;
            int menuDragControl = GUIUtility.GetControlID("AvatarControlHubMenuDrag".GetHashCode(), FocusType.Passive);
            int moveFrom = -1, moveTo = -1;
            bool finishMenuDrag = false;
            MenuControlEntry? selected = null, copied = null, cut = null, removed = null;
            bool collapseControl = false;
            SerializedObject menuDetailsToApply = null;
            string submenuNameToCreate = null;
            bool showMenuDetails = !_isMenuDragging;
            // Keep submenu and parameter names immediately left of the type, extending left.
            float metadataWidth = Mathf.Clamp((position.width - 400f) * 0.45f, 80f, 240f);
            EnsureMenuRowStyles();
            using var menuScrollView = new NestedScrollViewScope(ref _menuScrollPos, GUILayout.Height(_menuListHeight));
            if (e.type == EventType.Repaint) { _menuRowRects.Clear(); _menuRowIndices.Clear(); }
            foreach (var entry in entries)
            {
                if (entry.owner == null || entry.owner.controls == null || entry.index >= entry.owner.controls.Count || !ReferenceEquals(entry.control, entry.owner.controls[entry.index])) continue;
                var control = entry.control;
                bool isSubmenu = control != null && control.type == Control.ControlType.SubMenu;
                bool expanded = _selectedMenuOwner == entry.owner && _selectedMenuControlIndex == entry.index;
                Color background = GUI.backgroundColor;
                Color color = GUI.color;
                if (expanded) GUI.backgroundColor = new Color(0.4f, 0.7f, 1f, 0.7f);
                if (_isMenuDragging && entry.index == _menuDragSourceIndex) GUI.backgroundColor = new Color(0.3f, 0.7f, 1f, 1f);
                bool isCut = IsMenuControlCut(entry.owner, control);
                if (isCut) GUI.color = new Color(color.r, color.g, color.b, 0.45f);
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.BeginHorizontal(GUILayout.Height(25));
                GUILayout.Label(new GUIContent(searching ? "-" : "≡", searching ? "Clear search and type filters to drag controls." : ReorderDragTooltip), GUILayout.Width(17));
                Rect handle = GUILayoutUtility.GetLastRect();
                if (!searching)
                {
                    EditorGUIUtility.AddCursorRect(handle, MouseCursor.Pan);
                    if (e.type == EventType.MouseDown && e.button == 0 && _menuViewportRect.Contains(pointer) && handle.Contains(e.mousePosition))
                    {
                        _isMenuDragging = true;
                        _menuDragSourceIndex = _menuDropTarget = entry.index;
                        _menuDragOwner = currentMenu;
                        _menuDragOriginalControls = null;
                        _menuDragHasMoved = false;
                        _menuDragControlId = menuDragControl;
                        int sourceRow = _menuRowIndices.IndexOf(entry.index);
                        _menuDragPointerOffset = e.mousePosition.y - (sourceRow >= 0 ? _menuRowRects[sourceRow].y : handle.y);
                        GUIUtility.hotControl = menuDragControl;
                        GUI.FocusControl(null);
                        _menuRowRects.Clear();
                        _menuRowIndices.Clear();
                        Repaint();
                        e.Use();
                    }
                }
                if (GUILayout.Button(expanded ? "▼" : "►", EditorStyles.miniButton, GUILayout.Width(24)))
                {
                    if (expanded) collapseControl = true;
                    else selected = entry;
                }
                GUILayout.Label(new GUIContent(control != null ? control.icon : null), GUILayout.Width(23), GUILayout.Height(23));
                string display = control != null ? control.name ?? "" : "Missing control";
                if (string.IsNullOrEmpty(display)) display = "Unnamed control";
                string controlTooltip = (searching ? entry.path + "\n" : "") + MenuPlainText(display) + "\n" + MenuControlSummary(control);
                if (DrawMenuNamePreviewButton(display, controlTooltip))
                {
                    if (expanded) collapseControl = true;
                    else selected = entry;
                }
                string metadata = MenuControlSummary(control, false);
                GUILayout.Label(new GUIContent(metadata, MenuControlSummary(control)), _menuMetadataStyle, GUILayout.Width(metadataWidth));
                string typeText = control != null ? MenuControlTypeName(control.type) : "";
                float typeWidth = _menuMetadataStyle.CalcSize(new GUIContent(typeText)).x + 4f;
                GUILayout.Label(new GUIContent(typeText, typeText), _menuMetadataStyle, GUILayout.Width(typeWidth));
                if (isSubmenu)
                {
                    using (new EditorGUI.DisabledScope(control.subMenu == null))
                        if (GUILayout.Button("Open", GUILayout.Width(42))) { navigateTo = control.subMenu; navigateFrom = entry.owner; }
                }
                else GUILayout.Space(46);
                using (new EditorGUI.DisabledScope(control == null))
                {
                    if (GUILayout.Button(new GUIContent("Copy", "Copy this control. Referenced submenu assets are shared."), GUILayout.Width(42))) copied = entry;
                    if (GUILayout.Button(new GUIContent("Cut", isCut ? "Remove this control from the cut batch." :
                        "Add this control to the cut batch. Cut more controls, then paste them together."), GUILayout.Width(34))) cut = entry;
                }
                if (GUILayout.Button(new GUIContent("X", "Remove this control. Submenu and icon assets are kept."), GUILayout.Width(23))) removed = entry;
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                GUI.backgroundColor = background;
                GUI.color = color;
                Rect rowRect = GUILayoutUtility.GetLastRect();
                if (e.type == EventType.Repaint) { _menuRowRects.Add(rowRect); _menuRowIndices.Add(entry.index); }
                if (expanded && showMenuDetails)
                {
                    if (searching)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Label(entry.owner.name, EditorStyles.miniLabel, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Open Menu", GUILayout.Width(88))) navigateTo = entry.owner;
                        EditorGUILayout.EndHorizontal();
                    }
                    var submenu = DrawMenuControlDetails(entry.owner, entry.index, out menuDetailsToApply, out submenuNameToCreate);
                    if (submenu != null) { navigateTo = submenu; navigateFrom = entry.owner; }
                    GUILayout.Space(4);
                }
            }
            if (entries.Count == 0) EditorGUILayout.HelpBox(searching ? "No matching controls or parameters." : "Add a control to start building this menu.", MessageType.Info);
            if (_isMenuDragging && (e.type == EventType.MouseDrag || (e.rawType == EventType.MouseUp && e.button == 0)))
            {
                _menuDropTarget = _menuViewportRect.Contains(pointer) ? FindMenuDragTarget(e.mousePosition.y) : -1;
                if (_menuDropTarget >= 0 && _menuDropTarget != _menuDragSourceIndex)
                {
                    moveFrom = _menuDragSourceIndex; moveTo = _menuDropTarget;
                }
                finishMenuDrag = e.rawType == EventType.MouseUp;
                Repaint(); e.Use();
            }
            menuScrollView.Dispose();
            if (e.type == EventType.Repaint) _menuViewportRect = GUILayoutUtility.GetLastRect();
            DrawMenuEditorResizer();

            EditorGUILayout.EndVertical();

            // Apply after drawing every row, because serialization can replace Control instances.
            if (menuDetailsToApply != null)
            {
                var editedMenu = menuDetailsToApply.targetObject as VRCExpressionsMenu;
                var cutIndices = new Dictionary<MenuCutEntry, int>();
                if (editedMenu != null && editedMenu.controls != null)
                    foreach (var cutEntry in _menuCuts)
                        if (cutEntry.owner == editedMenu)
                        {
                            int cutIndex = editedMenu.controls.IndexOf(cutEntry.control);
                            if (cutIndex >= 0) cutIndices[cutEntry] = cutIndex;
                        }
                if (submenuNameToCreate != null)
                {
                    var controls = menuDetailsToApply.FindProperty("controls");
                    if (controls != null && _selectedMenuControlIndex >= 0 && _selectedMenuControlIndex < controls.arraySize)
                        CreateAndAssignSubMenu(controls.GetArrayElementAtIndex(_selectedMenuControlIndex).FindPropertyRelative("subMenu"), submenuNameToCreate);
                }
                if (menuDetailsToApply.ApplyModifiedProperties()) MarkMenuChanged(editedMenu);
                // Detail edits may replace Control instances while keeping the row order.
                foreach (var cutIndex in cutIndices)
                    if (editedMenu != null && editedMenu.controls != null && cutIndex.Value < editedMenu.controls.Count)
                        cutIndex.Key.control = editedMenu.controls[cutIndex.Value];
                if (_selectedMenuOwner == editedMenu && editedMenu != null && editedMenu.controls != null &&
                    _selectedMenuControlIndex >= 0 && _selectedMenuControlIndex < editedMenu.controls.Count)
                    _selectedMenuControlReference = editedMenu.controls[_selectedMenuControlIndex];
                menuDetailsToApply.Dispose();
            }
            if (removed.HasValue)
            {
                var item = removed.Value;
                _menuCuts.RemoveAll(entry => entry.owner == item.owner && ReferenceEquals(entry.control, item.owner.controls[item.index]));
                Undo.RegisterCompleteObjectUndo(item.owner, "Remove Menu Control");
                item.owner.controls.RemoveAt(item.index);
                if (_selectedMenuOwner == item.owner) ResetMenuControlSelection();
                MarkMenuChanged(item.owner);
            }
            else if (moveFrom >= 0 && moveTo >= 0 && moveFrom != moveTo && moveFrom < count && moveTo < count)
            {
                if (!_menuDragHasMoved)
                {
                    // Take the snapshot after pending detail edits have been applied.
                    _menuDragOriginalControls = currentMenu.controls.ToArray();
                    Undo.RegisterCompleteObjectUndo(currentMenu, "Reorder Menu Controls");
                }
                var item = currentMenu.controls[moveFrom];
                currentMenu.controls.RemoveAt(moveFrom);
                currentMenu.controls.Insert(moveTo, item);
                _menuDragSourceIndex = moveTo;
                _menuDragHasMoved = true;
                if (_selectedMenuOwner == currentMenu)
                    _selectedMenuControlIndex = currentMenu.controls.IndexOf(_selectedMenuControlReference);
                // Wait for fresh row positions before processing another move.
                _menuRowRects.Clear();
                _menuRowIndices.Clear();
                MarkMenuChanged(currentMenu);
            }
            if (copied.HasValue) CopyMenuControl(copied.Value.owner, copied.Value.index, false);
            if (cut.HasValue) CopyMenuControl(cut.Value.owner, cut.Value.index, true);
            if (paste) PasteMenuControl(currentMenu);
            if (navigateTo != null)
            {
                if (navigateFrom != null && navigateFrom != CurrentMenu) NavigateMenu(navigateFrom);
                NavigateMenu(navigateTo);
            }
            if (collapseControl) { ResetMenuControlSelection(); GUI.FocusControl(null); Repaint(); }
            else if (selected.HasValue) { SelectMenuControl(selected.Value.owner, selected.Value.index); GUI.FocusControl(null); Repaint(); }
            if (finishMenuDrag) ResetMenuDrag();
        }

        private void DrawMenuEditorResizer()
        {
            Event e = Event.current;
            Rect rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(6), GUILayout.ExpandWidth(true));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);
            if (e.type == EventType.Repaint) EditorGUI.DrawRect(new Rect(rect.x, rect.y + 2, rect.width, 2), new Color(0.15f, 0.15f, 0.15f));
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition)) { _isResizingMenuList = true; e.Use(); }
            if (_isResizingMenuList && e.type == EventType.MouseDrag)
            {
                _menuListHeight = Mathf.Clamp(_menuListHeight + e.delta.y, 60, 1000);
                Repaint(); e.Use();
            }
            if (_isResizingMenuList && e.rawType == EventType.MouseUp)
            {
                _isResizingMenuList = false;
                EditorPrefs.SetFloat(PrefMenuListHeightKey, _menuListHeight);
                e.Use();
            }
        }


        private VRCExpressionsMenu DrawMenuControlDetails(VRCExpressionsMenu menu, int index,
            out SerializedObject pendingChanges, out string submenuNameToCreate)
        {
            pendingChanges = null;
            submenuNameToCreate = null;
            if (menu == null || menu.controls == null || index < 0 || index >= menu.controls.Count)
                return null;
            if (menu.controls[index] == null)
            {
                EditorGUILayout.HelpBox("This control is empty. Remove it or replace it with a new control.", MessageType.Warning);
                return null;
            }

            var serializedMenu = new SerializedObject(menu);
            serializedMenu.Update();
            var controls = serializedMenu.FindProperty("controls");
            if (controls == null || index >= controls.arraySize)
            {
                serializedMenu.Dispose();
                return null;
            }
            var control = controls.GetArrayElementAtIndex(index);
            var typeProperty = control.FindPropertyRelative("type");
            var parameter = control.FindPropertyRelative("parameter").FindPropertyRelative("name");
            var value = control.FindPropertyRelative("value");
            var subMenu = control.FindPropertyRelative("subMenu");
            var axes = control.FindPropertyRelative("subParameters");
            var labels = control.FindPropertyRelative("labels");
            VRCExpressionsMenu menuToOpen = null;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            float nameColumnWidth = Mathf.Max(240f, (position.width - 60f) * 0.7f);
            EditorGUILayout.BeginHorizontal();
            DrawMenuNameWithColor(control.FindPropertyRelative("name"), index, nameColumnWidth);
            GUILayout.Space(8);
            GUILayout.Label("Icon", GUILayout.Width(32));
            EditorGUILayout.PropertyField(control.FindPropertyRelative("icon"), GUIContent.none,
                GUILayout.MinWidth(50), GUILayout.ExpandWidth(true), GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();

            var previousType = (VRCExpressionsMenu.Control.ControlType)typeProperty.intValue;
            bool showsActivationParameter = previousType == VRCExpressionsMenu.Control.ControlType.SubMenu ||
                previousType == VRCExpressionsMenu.Control.ControlType.RadialPuppet ||
                previousType == VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet ||
                previousType == VRCExpressionsMenu.Control.ControlType.FourAxisPuppet;
            string previousParameter = parameter.stringValue;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.BeginHorizontal(GUILayout.Width(nameColumnWidth));
            DrawMenuParameterDropdown(parameter, showsActivationParameter ? "Activation Parameter" : "Parameter",
                showsActivationParameter ? "Optional: 1 when open and in any submenu 0 when closed." : null, true);
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.Label("Type", GUILayout.Width(32));
            EditorGUI.BeginChangeCheck();
            typeProperty.intValue = (int)(Control.ControlType)HubEnumPopup("MenuControlType_" + control.serializedObject.targetObject.GetInstanceID() + "_" + index,
                null, (Control.ControlType)typeProperty.intValue, GUILayout.MinWidth(50), GUILayout.ExpandWidth(true));
            bool typeChanged = EditorGUI.EndChangeCheck();
            EditorGUILayout.EndHorizontal();
            var type = (VRCExpressionsMenu.Control.ControlType)typeProperty.intValue;
            bool radial = type == VRCExpressionsMenu.Control.ControlType.RadialPuppet;
            bool twoAxis = type == VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet;
            bool fourAxis = type == VRCExpressionsMenu.Control.ControlType.FourAxisPuppet;
            bool puppet = radial || twoAxis || fourAxis;
            bool opensMenu = puppet || type == VRCExpressionsMenu.Control.ControlType.SubMenu;
            int axisCount = radial ? 1 : twoAxis ? 2 : fourAxis ? 4 : 0;
            int labelCount = twoAxis || fourAxis ? 4 : 0;

            if (typeChanged)
            {
                ResizeMenuPuppetFields(axes, labels, axisCount, labelCount);
                if (type != VRCExpressionsMenu.Control.ControlType.SubMenu)
                    subMenu.objectReferenceValue = null;
            }

            if (!string.IsNullOrEmpty(parameter.stringValue))
            {
                VRCExpressionParameters.ValueType parameterType;
                bool knownType = TryGetMenuControlValueType(parameter.stringValue, out parameterType);
                string valueLabel = opensMenu ? "Activation Value" : "Value";
                if (knownType && parameterType == VRCExpressionParameters.ValueType.Bool)
                {
                    if (typeChanged || previousParameter != parameter.stringValue)
                        value.floatValue = 1f;
                    if (!opensMenu)
                    {
                        using (new EditorGUI.DisabledScope(true))
                            EditorGUILayout.Toggle(valueLabel, true);
                        EditorGUILayout.LabelField("Bool controls use On while active and Off when inactive.", EditorStyles.miniLabel);
                    }
                    if (value.floatValue != 1f)
                    {
                        EditorGUILayout.HelpBox("This Bool control has a value other than 1. Set it to 1 to match VRChat's menu behavior.", MessageType.Warning);
                        if (GUILayout.Button("Set Bool Value to 1")) value.floatValue = 1f;
                    }
                }
                else if (knownType && parameterType == VRCExpressionParameters.ValueType.Int)
                {
                    EditorGUI.BeginChangeCheck();
                    int newValue = EditorGUILayout.DelayedIntField(
                        new GUIContent(valueLabel, "VRChat integer menu values range from 0 to 255."),
                        Mathf.RoundToInt(value.floatValue));
                    if (EditorGUI.EndChangeCheck()) value.floatValue = Mathf.Clamp(newValue, 0, 255);
                }
                else if (!opensMenu || (knownType && parameterType == VRCExpressionParameters.ValueType.Float))
                {
                    EditorGUI.BeginChangeCheck();
                    float newValue = EditorGUILayout.DelayedFloatField(
                        new GUIContent(valueLabel, "VRChat float menu values range from -1 to 1."), value.floatValue);
                    if (EditorGUI.EndChangeCheck())
                        value.floatValue = knownType ? Mathf.Clamp(newValue, -1f, 1f) : newValue;
                }
            }

            if (puppet)
            {
                GUILayout.Space(4);
                if (!radial) EditorGUILayout.LabelField("Puppet Axes", EditorStyles.boldLabel);
                bool needsSetup = axes.arraySize != axisCount || labels.arraySize != labelCount;
                if (needsSetup)
                {
                    EditorGUILayout.HelpBox("This control's axis or label slots do not match its type. Set up the slots to edit all required fields.", MessageType.Warning);
                    if (GUILayout.Button("Set Up Puppet Slots"))
                        ResizeMenuPuppetFields(axes, labels, axisCount, labelCount);
                }
                string[] axisNames = radial ? new[] { "Rotation" } : twoAxis ? new[] { "Horizontal", "Vertical" } : new[] { "Up", "Right", "Down", "Left" };
                for (int axis = 0; axis < Mathf.Min(axisCount, axes.arraySize); axis++)
                    DrawMenuParameterDropdown(axes.GetArrayElementAtIndex(axis).FindPropertyRelative("name"), axisNames[axis],
                        radial ? "Axes use Float parameters from 0 to 1." : null);
                if (!radial)
                    EditorGUILayout.LabelField(twoAxis ? "Axes use Float parameters from -1 to 1." : "Axes use Float parameters from 0 to 1.", EditorStyles.miniLabel);
                if (labelCount > 0)
                {
                    GUILayout.Space(4);
                    EditorGUILayout.LabelField("Direction Labels", EditorStyles.boldLabel);
                    string[] directionNames = { "Up", "Right", "Down", "Left" };
                    for (int label = 0; label < Mathf.Min(labelCount, labels.arraySize); label++)
                        DrawPuppetLabel(labels.GetArrayElementAtIndex(label), directionNames[label]);
                }
            }

            if (type == VRCExpressionsMenu.Control.ControlType.SubMenu)
            {
                GUILayout.Space(4);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(subMenu, new GUIContent("Submenu"), GUILayout.ExpandWidth(true));
                using (new EditorGUI.DisabledScope(subMenu.objectReferenceValue == null))
                {
                    if (GUILayout.Button("Open", GUILayout.Width(55)))
                        menuToOpen = subMenu.objectReferenceValue as VRCExpressionsMenu;
                }
                EditorGUILayout.EndHorizontal();
                if (subMenu.objectReferenceValue == null)
                {
                    EditorGUILayout.BeginHorizontal();
                    _newSubMenuName = EditorGUILayout.DelayedTextField("New Submenu Name", _newSubMenuName);
                    if (GUILayout.Button("Create", GUILayout.Width(60)))
                    {
                        submenuNameToCreate = _newSubMenuName ?? "";
                        GUI.FocusControl(null);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndVertical();
            pendingChanges = serializedMenu;
            return menuToOpen;
        }

        private static void ResizeMenuPuppetFields(SerializedProperty axes, SerializedProperty labels, int axisCount, int labelCount)
        {
            int oldAxes = axes.arraySize;
            int oldLabels = labels.arraySize;
            axes.arraySize = axisCount;
            labels.arraySize = labelCount;
            // Unity duplicates the last element when arrays grow; new slots should start empty.
            for (int i = oldAxes; i < axisCount; i++)
                axes.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue = "";
            for (int i = oldLabels; i < labelCount; i++)
            {
                var label = labels.GetArrayElementAtIndex(i);
                label.FindPropertyRelative("name").stringValue = "";
                label.FindPropertyRelative("icon").objectReferenceValue = null;
            }
        }

        private bool TryGetMenuControlValueType(string parameterName, out VRCExpressionParameters.ValueType valueType)
        {
            valueType = VRCExpressionParameters.ValueType.Float;
            if (_vrcExpressionParametersAsset != null && _vrcExpressionParametersAsset.parameters != null)
                foreach (var parameter in _vrcExpressionParametersAsset.parameters)
                    if (parameter != null && parameter.name == parameterName)
                    {
                        valueType = parameter.valueType;
                        return true;
                    }
            var controller = GetAnimatorController();
            if (controller != null)
                foreach (var parameter in controller.parameters)
                    if (parameter.name == parameterName)
                    {
                        if (parameter.type == AnimatorControllerParameterType.Trigger) return false;
                        valueType = parameter.type == AnimatorControllerParameterType.Bool
                            ? VRCExpressionParameters.ValueType.Bool : parameter.type == AnimatorControllerParameterType.Int
                            ? VRCExpressionParameters.ValueType.Int : VRCExpressionParameters.ValueType.Float;
                        return true;
                    }
            return false;
        }

        private static string MenuNameColourHex(Color colour)
        {
            return "#" + (colour.a >= 1f ? ColorUtility.ToHtmlStringRGB(colour) : ColorUtility.ToHtmlStringRGBA(colour));
        }

        private sealed class MenuNamePreviewSpan
        {
            public string text;
            public float size;
            public float offset;
            public Color colour;
            public Color highlight = Color.clear;
            public bool bold, italic, underline, strike;
        }

        private static bool SameMenuNameFormats(MenuNameFormat[] left, MenuNameFormat[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i].opening != right[i].opening) return false;
            return true;
        }

        private static string MenuNameFormatValue(MenuNameFormat format)
        {
            int equals = format.opening.IndexOf('=');
            return equals < 0 ? "" : format.opening.Substring(equals + 1).TrimEnd('>').Trim().Trim('"', '\'');
        }

        private static float MenuNamePreviewSize(string value, float baseSize)
        {
            string number = value.TrimEnd('%');
            bool em = number.EndsWith("em", StringComparison.OrdinalIgnoreCase);
            if (em || number.EndsWith("px", StringComparison.OrdinalIgnoreCase)) number = number.Substring(0, number.Length - 2);
            if (!float.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float size) ||
                float.IsNaN(size) || float.IsInfinity(size)) return baseSize;
            if (value.EndsWith("%", StringComparison.Ordinal)) size = baseSize * size / 100f;
            else if (em) size *= baseSize;
            else if (number.StartsWith("+", StringComparison.Ordinal) || number.StartsWith("-", StringComparison.Ordinal)) size += baseSize;
            return Mathf.Clamp(size, 1f, 512f);
        }

        private bool DrawMenuNamePreviewButton(string raw, string tooltip)
        {
            Rect rect = GUILayoutUtility.GetRect(GUIContent.none, _menuControlNameStyle,
                GUILayout.MinWidth(65), GUILayout.ExpandWidth(true), GUILayout.Height(EditorGUIUtility.singleLineHeight));
            bool clicked = GUI.Button(rect, new GUIContent("", tooltip), _menuControlNameStyle);
            if (Event.current.type != EventType.Repaint) return clicked;
            var name = ReadMenuNameColours(raw);
            float baseSize = EditorStyles.label.fontSize > 0 ? EditorStyles.label.fontSize : 12f;
            var spans = new List<MenuNamePreviewSpan>();
            float tallest = baseSize;
            void AddSpan(string text, string colour, MenuNameFormat[] formats)
            {
                var span = new MenuNamePreviewSpan { text = text.Replace('\n', ' ').Replace('\r', ' '), size = baseSize, colour = EditorStyles.label.normal.textColor };
                if (colour != null && ColorUtility.TryParseHtmlString(colour, out Color parsed)) span.colour = parsed;
                foreach (var format in formats)
                {
                    switch (format.key)
                    {
                        case "b": span.bold = true; break;
                        case "i": span.italic = true; break;
                        case "u": span.underline = true; break;
                        case "s": span.strike = true; break;
                        case "size": span.size = MenuNamePreviewSize(MenuNameFormatValue(format), baseSize); break;
                        case "mark": if (ColorUtility.TryParseHtmlString(MenuNameFormatValue(format), out Color mark)) span.highlight = mark; break;
                        case "font-weight": if (int.TryParse(MenuNameFormatValue(format), out int weight)) span.bold |= weight >= 600; break;
                        case "uppercase": case "allcaps": span.text = span.text.ToUpperInvariant(); break;
                        case "lowercase": span.text = span.text.ToLowerInvariant(); break;
                        case "smallcaps":
                            if (span.text.Length > 0 && char.IsLower(span.text[0])) span.size *= 0.85f;
                            span.text = span.text.ToUpperInvariant();
                            break;
                        case "sup": span.size *= 0.75f; span.offset = -baseSize * 0.25f; break;
                        case "sub": span.size *= 0.75f; span.offset = baseSize * 0.2f; break;
                    }
                }
                tallest = Mathf.Max(tallest, span.size + 2f * Mathf.Abs(span.offset));
                spans.Add(span);
            }
            int markerIndex = 0;
            for (int start = 0; start <= name.text.Length;)
            {
                while (markerIndex < name.markers.Count && name.markers[markerIndex].position == start)
                {
                    var marker = name.markers[markerIndex++];
                    string key = MenuNameTagPattern.Match(marker.raw).Groups["name"].Value.ToLowerInvariant();
                    if (key == "br" || key == "space" || key == "nbsp" || key == "page") AddSpan(" ", marker.colour, marker.formats);
                    else if (key == "sprite" || key == "quad") AddSpan("◇", marker.colour, marker.formats);
                }
                if (start == name.text.Length) break;
                bool smallCaps = Array.Exists(name.formats[start], format => format.key == "smallcaps");
                int end = start + 1;
                while (end < name.text.Length && name.colours[end] == name.colours[start] && SameMenuNameFormats(name.formats[end], name.formats[start]) &&
                    (!smallCaps || char.IsLower(name.text[start]) == char.IsLower(name.text[end])) &&
                    (markerIndex >= name.markers.Count || name.markers[markerIndex].position > end)) end++;
                AddSpan(name.text.Substring(start, end - start), name.colours[start], name.formats[start]);
                start = end;
            }
            // Fit styled text inside the existing row; large size tags cannot grow the menu.
            float scale = Mathf.Min(1f, Mathf.Max(1f, rect.height - 2f) / tallest);
            var style = new GUIStyle(EditorStyles.label) { richText = false, wordWrap = false, alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0) };
            // Paint only: do not allocate additional control IDs during Repaint.
            GUI.BeginClip(rect);
            try
            {
                float x = 0f;
                foreach (var span in spans)
                {
                    if (x >= rect.width) break;
                    style.fontSize = Mathf.Max(1, Mathf.RoundToInt(span.size * scale));
                    style.fontStyle = span.bold ? (span.italic ? FontStyle.BoldAndItalic : FontStyle.Bold) : (span.italic ? FontStyle.Italic : FontStyle.Normal);
                    style.normal.textColor = span.colour;
                    var content = new GUIContent(span.text);
                    Vector2 size = style.CalcSize(content);
                    float y = (rect.height - size.y) * 0.5f + span.offset * scale;
                    var textRect = new Rect(x, y, size.x + 1f, size.y);
                    if (span.highlight.a > 0f) EditorGUI.DrawRect(textRect, span.highlight);
                    style.Draw(textRect, content, false, false, false, false);
                    if (span.underline) EditorGUI.DrawRect(new Rect(x, Mathf.Min(rect.height - 1f, y + size.y - 2f), size.x, 1f), span.colour);
                    if (span.strike) EditorGUI.DrawRect(new Rect(x, y + size.y * 0.55f, size.x, 1f), span.colour);
                    x += size.x;
                }
            }
            finally { GUI.EndClip(); }
            return clicked;
        }

        private static void AppendMenuNameText(MenuNameColours name, System.Text.StringBuilder text, string part,
            string colour, MenuNameFormat[] formats, int rawStart, List<int> rawTextIndices)
        {
            text.Append(part);
            for (int i = 0; i < part.Length; i++)
            {
                name.colours.Add(colour);
                name.formats.Add(formats);
                rawTextIndices?.Add(rawStart + i);
            }
        }

        private static MenuNameColours ReadMenuNameColours(string raw, string defaultColour = null,
            MenuNameFormat[] defaultFormats = null, List<int> rawTextIndices = null)
        {
            raw = raw ?? "";
            var name = new MenuNameColours { emptyColour = defaultColour, emptyFormats = defaultFormats ?? Array.Empty<MenuNameFormat>() };
            var text = new System.Text.StringBuilder();
            var parents = new Stack<string>();
            var formats = new List<MenuNameFormat>(name.emptyFormats);
            var snapshot = formats.ToArray();
            string colour = defaultColour;
            int position = 0;
            foreach (System.Text.RegularExpressions.Match tag in MenuNameTagPattern.Matches(raw))
            {
                AppendMenuNameText(name, text, raw.Substring(position, tag.Index - position), colour, snapshot, position, rawTextIndices);
                string key = tag.Groups["name"].Value.ToLowerInvariant();
                bool closing = tag.Groups["close"].Success;
                bool noParse = formats.Exists(format => format.key == "noparse");
                if (noParse && !(closing && key == "noparse"))
                    AppendMenuNameText(name, text, tag.Value, colour, snapshot, tag.Index, rawTextIndices);
                else if (key == "color" || tag.Groups["hex"].Success)
                {
                    string value = tag.Groups["hex"].Success ? tag.Groups["hex"].Value :
                        tag.Groups["args"].Value.Trim().TrimStart('=').Trim().Trim('"', '\'');
                    if (closing) colour = parents.Count > 0 ? parents.Pop() : defaultColour;
                    else if (ColorUtility.TryParseHtmlString(value, out Color parsed))
                    {
                        parents.Push(colour);
                        colour = MenuNameColourHex(parsed);
                        name.emptyColour = colour;
                    }
                    else AppendMenuNameText(name, text, tag.Value, colour, snapshot, tag.Index, rawTextIndices);
                }
                else if (MenuNameStyleTags.Contains(key))
                {
                    if (closing)
                    {
                        int match = formats.FindLastIndex(format => format.key == key);
                        if (match >= 0) formats.RemoveAt(match);
                        else AppendMenuNameText(name, text, tag.Value, colour, snapshot, tag.Index, rawTextIndices);
                    }
                    else
                    {
                        formats.Add(new MenuNameFormat { key = key, opening = tag.Value, closing = "</" + key + ">" });
                        name.emptyFormats = formats.ToArray();
                    }
                    snapshot = formats.ToArray();
                }
                else if (MenuNameStandaloneTags.Contains(key))
                    name.markers.Add(new MenuNameMarker { position = text.Length, raw = tag.Value, colour = colour, formats = snapshot });
                else AppendMenuNameText(name, text, tag.Value, colour, snapshot, tag.Index, rawTextIndices);
                position = tag.Index + tag.Length;
            }
            AppendMenuNameText(name, text, raw.Substring(position), colour, snapshot, position, rawTextIndices);
            name.text = text.ToString();
            return name;
        }

        private static string WriteMenuNameColours(MenuNameColours name)
        {
            var result = new System.Text.StringBuilder();
            var active = new List<MenuNameFormat>();
            void SwitchFormat(string colour, MenuNameFormat[] formats)
            {
                var desired = new List<MenuNameFormat>();
                // Keep layout/style scopes open across colour changes. Only noparse needs
                // colour outside it, otherwise colour tags would become literal text.
                int noParseIndex = Array.FindIndex(formats, format => format.key == "noparse");
                int colourIndex = noParseIndex >= 0 ? noParseIndex : formats.Length;
                for (int i = 0; i < colourIndex; i++) desired.Add(formats[i]);
                if (colour != null) desired.Add(new MenuNameFormat { opening = "<color=" + colour + ">", closing = "</color>" });
                for (int i = colourIndex; i < formats.Length; i++) desired.Add(formats[i]);
                int shared = 0;
                while (shared < active.Count && shared < desired.Count && active[shared].opening == desired[shared].opening) shared++;
                for (int i = active.Count - 1; i >= shared; i--) result.Append(active[i].closing);
                for (int i = shared; i < desired.Count; i++) result.Append(desired[i].opening);
                active = desired;
            }
            int markerIndex = 0;
            for (int i = 0; i <= name.text.Length; i++)
            {
                while (markerIndex < name.markers.Count && name.markers[markerIndex].position == i)
                {
                    var marker = name.markers[markerIndex++];
                    SwitchFormat(marker.colour, marker.formats);
                    result.Append(marker.raw);
                }
                if (i == name.text.Length) break;
                if (i == 0 || name.colours[i] != name.colours[i - 1] || name.formats[i] != name.formats[i - 1] ||
                    (markerIndex > 0 && name.markers[markerIndex - 1].position == i))
                    SwitchFormat(name.colours[i], name.formats[i]);
                result.Append(name.text[i]);
            }
            if (name.text.Length == 0 && name.markers.Count == 0) SwitchFormat(name.emptyColour, name.emptyFormats);
            for (int i = active.Count - 1; i >= 0; i--) result.Append(active[i].closing);
            return result.ToString();
        }

        private static MenuNameColours ReplaceMenuNameText(MenuNameColours previous, string edited,
            out int changeStart, out int removedLength, out int insertedLength)
        {
            changeStart = 0;
            while (changeStart < previous.text.Length && changeStart < edited.Length && previous.text[changeStart] == edited[changeStart])
                changeStart++;
            int suffix = 0;
            while (suffix < previous.text.Length - changeStart && suffix < edited.Length - changeStart &&
                previous.text[previous.text.Length - 1 - suffix] == edited[edited.Length - 1 - suffix]) suffix++;
            removedLength = previous.text.Length - changeStart - suffix;
            string inheritedColour = previous.colours.Count == 0 ? previous.emptyColour :
                previous.colours[Mathf.Min(changeStart, previous.colours.Count - 1)];
            var inheritedFormats = previous.formats.Count == 0 ? previous.emptyFormats :
                previous.formats[Mathf.Min(changeStart, previous.formats.Count - 1)];
            var inserted = ReadMenuNameColours(edited.Substring(changeStart, edited.Length - changeStart - suffix), inheritedColour, inheritedFormats);
            insertedLength = inserted.text.Length;
            var name = new MenuNameColours
            {
                text = previous.text.Substring(0, changeStart) + inserted.text + previous.text.Substring(previous.text.Length - suffix),
                emptyColour = inserted.emptyColour,
                emptyFormats = inserted.emptyFormats
            };
            name.colours.AddRange(previous.colours.GetRange(0, changeStart));
            name.colours.AddRange(inserted.colours);
            name.colours.AddRange(previous.colours.GetRange(previous.colours.Count - suffix, suffix));
            name.formats.AddRange(previous.formats.GetRange(0, changeStart));
            name.formats.AddRange(inserted.formats);
            name.formats.AddRange(previous.formats.GetRange(previous.formats.Count - suffix, suffix));
            foreach (var marker in previous.markers)
                if (marker.position <= changeStart) name.markers.Add(marker);
            foreach (var marker in inserted.markers)
                name.markers.Add(new MenuNameMarker { position = marker.position + changeStart, raw = marker.raw, colour = marker.colour, formats = marker.formats });
            foreach (var marker in previous.markers)
                if (marker.position > changeStart && marker.position >= changeStart + removedLength)
                    name.markers.Add(new MenuNameMarker { position = marker.position + insertedLength - removedLength,
                        raw = marker.raw, colour = marker.colour, formats = marker.formats });
            return name;
        }

        private void SetMenuNameColour(MenuNameColours name, string colour)
        {
            int start = _menuNameColourLength < 0 ? 0 : Mathf.Clamp(_menuNameColourStart, 0, name.text.Length);
            int end = _menuNameColourLength < 0 ? name.text.Length : Mathf.Min(start + _menuNameColourLength, name.text.Length);
            for (int i = start; i < end; i++) name.colours[i] = colour;
            foreach (var marker in name.markers)
                if (marker.position >= start && (marker.position < end || _menuNameColourLength < 0)) marker.colour = colour;
            name.emptyColour = colour;
        }

        private void SelectMenuNameColourPart(string controlId, int start, int length)
        {
            _menuNameColourControl = controlId;
            _menuNameColourStart = start;
            _menuNameColourLength = length;
            GUI.FocusControl(null);
            Repaint();
        }

        private void ApplyMenuNameFormat(MenuNameColours name, string key, string value, bool remove)
        {
            int start = _menuNameColourLength < 0 ? 0 : Mathf.Clamp(_menuNameColourStart, 0, name.text.Length);
            int end = _menuNameColourLength < 0 ? name.text.Length : Mathf.Min(start + _menuNameColourLength, name.text.Length);
            MenuNameFormat[] UpdateFormats(MenuNameFormat[] previous)
            {
                var formats = new List<MenuNameFormat>(previous);
                if (key == "*")
                {
                    formats.RemoveAll(format => MenuNameTextStyleTags.Contains(format.key));
                    return formats.ToArray();
                }
                formats.RemoveAll(format => format.key == key || (!remove &&
                    ((key == "sub" || key == "sup") && (format.key == "sub" || format.key == "sup") ||
                    (key == "uppercase" || key == "lowercase" || key == "smallcaps") &&
                    (format.key == "uppercase" || format.key == "allcaps" || format.key == "lowercase" || format.key == "smallcaps"))));
                if (!remove)
                {
                    int noParseIndex = formats.FindIndex(format => format.key == "noparse");
                    formats.Insert(noParseIndex >= 0 ? noParseIndex : formats.Count,
                        new MenuNameFormat { key = key, opening = "<" + key + (value == null ? "" : "=" + value) + ">", closing = "</" + key + ">" });
                }
                return formats.ToArray();
            }
            for (int i = start; i < end; i++) name.formats[i] = UpdateFormats(name.formats[i]);
            foreach (var marker in name.markers)
                if (marker.position >= start && (marker.position < end || _menuNameColourLength < 0)) marker.formats = UpdateFormats(marker.formats);
            name.emptyFormats = UpdateFormats(name.emptyFormats);
        }

        private void ShowMenuNameFormatting(string controlId, MenuNameColours name)
        {
            var options = new HubDropdownMenu(this, true);
            int start = _menuNameColourLength < 0 ? 0 : Mathf.Clamp(_menuNameColourStart, 0, name.text.Length);
            int length = _menuNameColourLength < 0 ? name.text.Length : Mathf.Min(_menuNameColourLength, name.text.Length - start);
            bool rawMode = _menuNameRawControl == controlId;
            bool HasFormat(string key)
            {
                if (length == 0) return Array.Exists(name.emptyFormats, format => format.key == key);
                for (int i = start; i < start + length; i++)
                    if (!Array.Exists(name.formats[i], format => format.key == key)) return false;
                return true;
            }
            void Add(string label, string key, string value = null, bool remove = false, bool toggle = false)
            {
                bool selected = toggle && HasFormat(key);
                if (rawMode) { options.AddDisabledItem(new GUIContent(label)); return; }
                options.AddItem(new GUIContent(label), selected, () =>
                {
                    SelectMenuNameColourPart(controlId, start, length);
                    _menuNameFormatControl = controlId;
                    _menuNameFormatKey = key;
                    _menuNameFormatValue = value;
                    _menuNameFormatRemove = remove || selected;
                });
            }
            options.AddDisabledItem(new GUIContent(_menuNameColourLength < 0 ? "Format whole name" : "Format selected text"));
            options.AddSeparator("");
            Add("Bold", "b", toggle: true);
            Add("Italic", "i", toggle: true);
            Add("Underline", "u", toggle: true);
            Add("Strikethrough", "s", toggle: true);
            Add("Highlight", "mark", "#FFFF0080", toggle: true);
            Add("Superscript", "sup", toggle: true);
            Add("Subscript", "sub", toggle: true);
            Add("Letter case/Uppercase", "uppercase", toggle: true);
            Add("Letter case/Lowercase", "lowercase", toggle: true);
            Add("Letter case/Small caps", "smallcaps", toggle: true);
            Add("Size/Default", "size", remove: true);
            foreach (string size in new[] { "50%", "75%", "100%", "125%", "150%", "200%" }) Add("Size/" + size, "size", size);
            options.AddSeparator("");
            Add("Remove text styles", "*", remove: true);
            AddMenuNameRawEditingOption(options, controlId);
            options.ShowAsContext();
        }

        private void AddMenuNameRawEditingOption(HubDropdownMenu options, string controlId)
        {
            options.AddItem(new GUIContent("Edit rich-text tags"), _menuNameRawControl == controlId,
                () => ToggleMenuNameRawEditing(controlId));
        }

        private void ToggleMenuNameRawEditing(string controlId)
        {
            _menuNameRawControl = _menuNameRawControl == controlId ? null : controlId;
            GUI.FocusControl(null);
            Repaint();
        }

        private void ShowMenuNameColourParts(string controlId, MenuNameColours name)
        {
            var options = new HubDropdownMenu(this);
            int section = 1;
            for (int start = 0; start < name.text.Length;)
            {
                int end = start + 1;
                while (end < name.text.Length && name.colours[end] == name.colours[start]) end++;
                int partStart = start;
                int partLength = end - start;
                string label = name.text.Substring(start, partLength).Trim().Replace('\n', ' ').Replace('\r', ' ');
                if (name.colours[start] != null && !string.IsNullOrWhiteSpace(label))
                {
                    if (label.Length > 35) label = label.Substring(0, 32) + "…";
                    options.AddItem(new GUIContent(section + ". " + label), false,
                        () => SelectMenuNameColourPart(controlId, partStart, partLength), true,
                        () => _menuNameColourStart == partStart && _menuNameColourLength == partLength);
                    section++;
                }
                start = end;
            }
            if (section > 1) options.AddSeparator("");
            bool HasColour()
            {
                int start = _menuNameColourLength < 0 ? 0 : Mathf.Clamp(_menuNameColourStart, 0, name.text.Length);
                int end = _menuNameColourLength < 0 ? name.text.Length : Mathf.Min(start + _menuNameColourLength, name.text.Length);
                if (name.text.Length == 0 && name.emptyColour != null) return true;
                for (int i = start; i < end; i++) if (name.colours[i] != null) return true;
                return false;
            }
            options.AddItem(new GUIContent("Remove colour"), false, () =>
            {
                // Apply after drawing: this row's SerializedObject does not outlive OnGUI.
                _menuNameClearColourControl = controlId;
                Repaint();
            }, enabledNow: HasColour);
            options.AddSeparator("");
            AddMenuNameRawEditingOption(options, controlId);
            options.ShowAsContext();
        }

        private string DrawMenuNameTextField(string controlId, string text, out Rect textRect)
        {
            textRect = GUILayoutUtility.GetRect(new GUIContent(text), EditorStyles.textField,
                GUILayout.MinWidth(50), GUILayout.ExpandWidth(true), GUILayout.Height(EditorGUIUtility.singleLineHeight));
            Event e = Event.current;
            EventType inputType = e.type;
            if (inputType == EventType.MouseDown && e.button == 0)
                _menuNameSelectAllOnClickControl = GUI.enabled && textRect.Contains(e.mousePosition) &&
                    GUI.GetNameOfFocusedControl() != controlId ? controlId : null;

            GUI.SetNextControlName(controlId);
            string edited = GUI.TextField(textRect, text, EditorStyles.textField);
            if (_menuNameSelectAllOnClickControl == controlId)
            {
                // Wait for a completed click so dragging can still select just part of a name.
                if (GUI.GetNameOfFocusedControl() != controlId || inputType == EventType.MouseDrag || inputType == EventType.KeyDown)
                    _menuNameSelectAllOnClickControl = null;
                else if (inputType == EventType.MouseUp && e.button == 0)
                {
                    var editor = GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor;
                    editor?.SelectAll();
                    _menuNameSelectAllOnClickControl = null;
                    Repaint();
                }
            }
            return edited;
        }

        private void DrawMenuNameWithColor(SerializedProperty nameProp, int index, float width)
        {
            string controlId = "MenuName_" + nameProp.serializedObject.targetObject.GetInstanceID() + "_" + index;
            var name = ReadMenuNameColours(nameProp.stringValue);
            if (_menuNameColourControl != controlId)
            {
                _menuNameColourControl = controlId;
                _menuNameColourStart = 0;
                _menuNameColourLength = -1;
                _menuNameClearColourControl = null;
                _menuNameFormatControl = null;
                _menuNameRawControl = null;
                for (int partStart = 0; partStart < name.text.Length;)
                {
                    int partEnd = partStart + 1;
                    while (partEnd < name.text.Length && name.colours[partEnd] == name.colours[partStart]) partEnd++;
                    if (name.colours[partStart] != null && !string.IsNullOrWhiteSpace(name.text.Substring(partStart, partEnd - partStart)))
                    {
                        _menuNameColourStart = partStart;
                        _menuNameColourLength = partEnd - partStart;
                        break;
                    }
                    partStart = partEnd;
                }
            }
            if (_menuNameColourLength >= 0)
            {
                if (_menuNameColourStart >= name.text.Length)
                {
                    _menuNameColourStart = 0;
                    _menuNameColourLength = -1;
                }
                else
                    _menuNameColourLength = Mathf.Clamp(_menuNameColourLength, 1, name.text.Length - _menuNameColourStart);
            }
            bool changed = false;
            if (_menuNameClearColourControl == controlId)
            {
                SetMenuNameColour(name, null);
                _menuNameClearColourControl = null;
                changed = true;
            }
            if (_menuNameFormatControl == controlId)
            {
                ApplyMenuNameFormat(name, _menuNameFormatKey, _menuNameFormatValue, _menuNameFormatRemove);
                _menuNameFormatControl = null;
                changed = true;
            }
            bool rawMode = _menuNameRawControl == controlId;
            int start = _menuNameColourLength < 0 ? 0 : _menuNameColourStart;
            int end = _menuNameColourLength < 0 ? name.text.Length : Mathf.Min(start + _menuNameColourLength, name.text.Length);
            string targetColour = name.colours.Count > 0 ? name.colours[start] : name.emptyColour;
            Color displayColour = Color.white;
            if (targetColour != null) ColorUtility.TryParseHtmlString(targetColour, out displayColour);
            bool mixed = false;
            for (int i = start + 1; i < end; i++)
                if (name.colours[i] != targetColour) { mixed = true; break; }
            string target = _menuNameColourLength < 0 ? "the whole name" : "\"" + name.text.Substring(start, end - start) + "\"";
            string colourTip = "Change the colour of " + target + ". Highlight text in Name, then use this picker to colour just that text.";

            EditorGUILayout.BeginHorizontal(GUILayout.Width(width));
            GUILayout.Label("Name", GUILayout.Width(44));
            bool previousMixed = EditorGUI.showMixedValue;
            EditorGUI.showMixedValue = mixed;
            EditorGUI.BeginChangeCheck();
            displayColour = EditorGUILayout.ColorField(GUIContent.none, displayColour, false, true, false, GUILayout.Width(50));
            if (EditorGUI.EndChangeCheck())
            {
                SetMenuNameColour(name, MenuNameColourHex(displayColour));
                changed = true;
            }
            EditorGUI.showMixedValue = previousMixed;
            GUI.Label(GUILayoutUtility.GetLastRect(), new GUIContent("", colourTip), GUIStyle.none);
            if (HubDropdownButtonWithId(controlId + "_ColourParts", new GUIContent("▼", "Choose an existing coloured section, remove its colour, or edit rich-text tags."), EditorStyles.miniButton, GUILayout.Width(20)))
                ShowMenuNameColourParts(controlId, name);
            if (HubDropdownButtonWithId(controlId + "_Formatting", new GUIContent(rawMode ? "<> ▾" : "Aa ▾",
                "Format highlighted text, or edit the full rich-text tags. Advanced font and layout tags are preserved; their appearance can differ in VRChat."), EditorStyles.miniButton, GUILayout.Width(38)))
                ShowMenuNameFormatting(controlId, name);
            if (rawMode)
            {
                // Save picker/dropdown edits before drawing the raw text so neither overwrites the other.
                if (changed)
                {
                    nameProp.stringValue = WriteMenuNameColours(name);
                    GUI.changed = true;
                }
                EventType rawInputType = Event.current.type;
                nameProp.stringValue = DrawMenuNameTextField(controlId + "_Raw", nameProp.stringValue, out Rect rawTextRect);
                bool selectingRaw = rawInputType == EventType.KeyDown || rawInputType == EventType.ExecuteCommand ||
                    rawInputType == EventType.MouseDrag || rawInputType == EventType.MouseUp ||
                    (rawInputType == EventType.MouseDown && rawTextRect.Contains(Event.current.mousePosition));
                if (selectingRaw && GUI.GetNameOfFocusedControl() == controlId + "_Raw" && GUIUtility.keyboardControl != 0)
                {
                    var editor = GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor;
                    if (editor != null && editor.cursorIndex != editor.selectIndex)
                    {
                        int rawStart = Mathf.Min(editor.cursorIndex, editor.selectIndex);
                        int rawEnd = Mathf.Max(editor.cursorIndex, editor.selectIndex);
                        var rawTextIndices = new List<int>();
                        ReadMenuNameColours(nameProp.stringValue, rawTextIndices: rawTextIndices);
                        // Map only visible characters; a selection may include complete or partial tags.
                        int highlightedStart = 0;
                        while (highlightedStart < rawTextIndices.Count && rawTextIndices[highlightedStart] < rawStart) highlightedStart++;
                        int highlightedEnd = highlightedStart;
                        while (highlightedEnd < rawTextIndices.Count && rawTextIndices[highlightedEnd] < rawEnd) highlightedEnd++;
                        if (highlightedEnd > highlightedStart)
                        {
                            _menuNameColourStart = highlightedStart;
                            _menuNameColourLength = highlightedEnd - highlightedStart;
                        }
                    }
                }
                GUI.Label(rawTextRect, new GUIContent("", "Full name with rich-text tags. Highlight name text and use the colour picker, or choose a coloured section from its dropdown. Turn off Edit rich-text tags in either dropdown to return to the plain name editor."), GUIStyle.none);
                EditorGUILayout.EndHorizontal();
                return;
            }

            EventType inputType = Event.current.type;
            string edited = DrawMenuNameTextField(controlId, name.text, out Rect textRect);
            if (edited != name.text)
            {
                name = ReplaceMenuNameText(name, edited, out int changeStart, out int removedLength, out int insertedLength);
                if (_menuNameColourLength >= 0)
                {
                    int targetEnd = _menuNameColourStart + _menuNameColourLength;
                    int changeEnd = changeStart + removedLength;
                    int difference = insertedLength - removedLength;
                    if (_menuNameColourStart > changeStart)
                        _menuNameColourStart = _menuNameColourStart >= changeEnd ? _menuNameColourStart + difference : changeStart;
                    if (targetEnd >= changeEnd) targetEnd += difference;
                    else if (targetEnd > changeStart) targetEnd = changeStart + insertedLength;
                    _menuNameColourLength = Mathf.Max(0, targetEnd - _menuNameColourStart);
                    if (_menuNameColourLength == 0) _menuNameColourLength = -1;
                }
                changed = true;
            }
            if (GUI.GetNameOfFocusedControl() == controlId && GUIUtility.keyboardControl != 0)
            {
                var editor = GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl) as TextEditor;
                if (editor != null)
                {
                    if (edited != name.text)
                    {
                        // Pasted rich-text tags become formatting while the caret stays in the plain text.
                        int caret = ReadMenuNameColours(edited.Substring(0, Mathf.Clamp(editor.cursorIndex, 0, edited.Length))).text.Length;
                        editor.text = name.text;
                        editor.cursorIndex = editor.selectIndex = Mathf.Clamp(caret, 0, name.text.Length);
                    }
                    bool selecting = inputType == EventType.KeyDown || inputType == EventType.ExecuteCommand ||
                        inputType == EventType.MouseDrag || inputType == EventType.MouseUp ||
                        (inputType == EventType.MouseDown && textRect.Contains(Event.current.mousePosition));
                    if (selecting)
                    {
                        int highlightedStart = Mathf.Clamp(Mathf.Min(editor.cursorIndex, editor.selectIndex), 0, name.text.Length);
                        int highlightedLength = Mathf.Clamp(Mathf.Max(editor.cursorIndex, editor.selectIndex), 0, name.text.Length) - highlightedStart;
                        if (highlightedLength > 0)
                        {
                            // Keep the range when opening the picker takes focus away from the text field.
                            _menuNameColourStart = highlightedStart;
                            _menuNameColourLength = highlightedLength;
                        }
                    }
                }
            }
            GUI.Label(textRect, new GUIContent("", "Highlight text, then use the colour picker or Aa menu to format that selection. The colour dropdown lists existing coloured sections."), GUIStyle.none);
            if (changed)
            {
                nameProp.stringValue = WriteMenuNameColours(name);
                GUI.changed = true;
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawMenuParameterDropdown(SerializedProperty paramNameProp, string labelText = "Parameter", string tooltip = null, bool compactLabel = false)
        {
            if (paramNameProp == null) return;
            string currentVal = paramNameProp.stringValue;
            string controlId = "MenuParam_" + paramNameProp.serializedObject.targetObject.GetInstanceID() + "_" + paramNameProp.propertyPath;
            if (_pendingComboChangeId == controlId && _pendingComboChangeValue != null)
            {
                paramNameProp.stringValue = currentVal = _pendingComboChangeValue;
                _pendingComboChangeId = null;
                _pendingComboChangeValue = null;
                GUI.FocusControl(null);
                GUI.changed = true;
            }
            EditorGUILayout.BeginHorizontal();
            float labelWidth = compactLabel ? GUI.skin.label.CalcSize(new GUIContent(labelText)).x + 4f : EditorGUIUtility.labelWidth - 4;
            GUILayout.Label(labelText, GUILayout.Width(labelWidth));
            GUI.SetNextControlName(controlId);
            string edited = EditorGUILayout.TextField(currentVal, GUILayout.MinWidth(50), GUILayout.ExpandWidth(true));
            if (edited != currentVal) paramNameProp.stringValue = edited;
            if (HubDropdownButtonWithId(controlId, new GUIContent("▼"), EditorStyles.miniButton, GUILayout.Width(22)))
            {
                var options = new HubDropdownMenu(this);
                options.AddItem(new GUIContent("None (no parameter)"), string.IsNullOrEmpty(currentVal), () =>
                {
                    _pendingComboChangeId = controlId;
                    _pendingComboChangeValue = "";
                    Repaint();
                });
                var names = new HashSet<string>(StringComparer.Ordinal);
                var controller = GetAnimatorController();
                var animatorParameters = controller != null ? controller.parameters : Array.Empty<AnimatorControllerParameter>();
                var vrcNames = GetVrcParameterNames(_vrcExpressionParametersAsset);
                options.AddSeparator("");
                foreach (var row in BuildParameterOrder(animatorParameters, vrcNames))
                {
                    if (row.vrcIndex < 0) continue;
                    string name = vrcNames[row.vrcIndex];
                    if (string.IsNullOrEmpty(name) || !names.Add(name)) continue;
                    string chosenName = name;
                    options.AddItem(new GUIContent(name), name == currentVal, () =>
                    {
                        _pendingComboChangeId = controlId;
                        _pendingComboChangeValue = chosenName;
                        Repaint();
                    });
                }
                if (names.Count == 0) options.AddDisabledItem(new GUIContent("Assign VRC Parameters above for suggestions"));
                options.ShowAsContext();
            }
            EditorGUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(tooltip))
                GUI.Label(GUILayoutUtility.GetLastRect(), new GUIContent("", tooltip), GUIStyle.none);
        }

        private void DrawPuppetLabel(SerializedProperty labelProp, string title)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(labelProp.FindPropertyRelative("name"), new GUIContent(title));
            EditorGUILayout.PropertyField(labelProp.FindPropertyRelative("icon"), GUIContent.none, GUILayout.Width(75));
            EditorGUILayout.EndHorizontal();
        }

        private void CreateAndAssignSubMenu(SerializedProperty subMenuProp, string fileName)
        {
            VRCExpressionsMenu current = subMenuProp.serializedObject.targetObject as VRCExpressionsMenu;
            if (current == null) return;

            string currentPath = AssetDatabase.GetAssetPath(current);
            string directory = (System.IO.Path.GetDirectoryName(currentPath) ?? "").Replace("\\", "/");
            string sanitizedName = string.IsNullOrWhiteSpace(fileName) ? "New SubMenu" : fileName.Trim();
            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars()) sanitizedName = sanitizedName.Replace(invalid, '_');
            sanitizedName = sanitizedName.Replace('/', '_').Replace('\\', '_').Trim('.', ' ');
            if (string.IsNullOrEmpty(sanitizedName)) sanitizedName = "New SubMenu";
            if (System.Text.RegularExpressions.Regex.IsMatch(sanitizedName, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) sanitizedName = "_" + sanitizedName;
            string targetPath;
            if ((directory == "Assets" || directory.StartsWith("Assets/", StringComparison.Ordinal)) && AssetDatabase.IsValidFolder(directory))
                targetPath = directory + "/" + sanitizedName + ".asset";
            else
                targetPath = EditorUtility.SaveFilePanelInProject("Create Submenu", sanitizedName, "asset", "Choose a location inside Assets for the new submenu.", "Assets");
            if (string.IsNullOrEmpty(targetPath)) return;
            targetPath = AssetDatabase.GenerateUniqueAssetPath(targetPath);

            VRCExpressionsMenu newMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            newMenu.name = sanitizedName;
            newMenu.controls = new List<VRCExpressionsMenu.Control>();
            AssetDatabase.CreateAsset(newMenu, targetPath);
            if (!AssetDatabase.Contains(newMenu))
            {
                DestroyImmediate(newMenu);
                EditorUtility.DisplayDialog("Could Not Create Submenu", "Choose a writable folder inside Assets and try again.", "OK");
                return;
            }
            subMenuProp.objectReferenceValue = newMenu;
            subMenuProp.serializedObject.ApplyModifiedProperties();
            MarkMenuChanged(current);
            AssetDatabase.SaveAssets();
        }

        private void AddVrcParameter(AnimatorController controller, UnityEngine.Object exprParamsAsset, string paramName)
        {
            if (controller == null || exprParamsAsset == null || string.IsNullOrEmpty(paramName)) return;

            if (IsAnimatorOnlyParameter(paramName))
            {
                RemoveAnimatorOnlyParametersFromVrc();
                if (_hideVrcBuiltInParameters && paramName == "BlendTreeWeight")
                {
                    EnsureBlendTreeWeightFloatParameter(controller);
                }
                return;
            }

            SerializedObject so = new SerializedObject(exprParamsAsset);
            SerializedProperty parametersProp = so.FindProperty("parameters");
            if (parametersProp == null || !parametersProp.isArray) return;

            for (int i = 0; i < parametersProp.arraySize; i++)
            {
                if (parametersProp.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue == paramName)
                    return;
            }

            AnimatorControllerParameter targetParam = null;
            foreach (var p in controller.parameters)
            {
                if (p.name == paramName)
                {
                    targetParam = p;
                    break;
                }
            }
            if (targetParam == null) return;

            Undo.RegisterCompleteObjectUndo(exprParamsAsset, "Add VRC Expression Parameter");

            parametersProp.InsertArrayElementAtIndex(parametersProp.arraySize);
            SerializedProperty newElement = parametersProp.GetArrayElementAtIndex(parametersProp.arraySize - 1);
            newElement.FindPropertyRelative("name").stringValue = targetParam.name;

            int typeVal = 1;
            if (targetParam.type == AnimatorControllerParameterType.Int) typeVal = 0;
            else if (targetParam.type == AnimatorControllerParameterType.Bool) typeVal = 2;

            newElement.FindPropertyRelative("valueType").intValue = typeVal;
            newElement.FindPropertyRelative("defaultValue").floatValue =
                targetParam.type == AnimatorControllerParameterType.Bool ? (targetParam.defaultBool ? 1f : 0f) :
                targetParam.type == AnimatorControllerParameterType.Int ? targetParam.defaultInt : targetParam.defaultFloat;
            newElement.FindPropertyRelative("saved").boolValue = true;

            SerializedProperty networkSyncedProp = newElement.FindPropertyRelative("networkSynced");
            if (networkSyncedProp != null)
            {
                networkSyncedProp.boolValue = true;
            }

            ReorderVRCParameters(controller, parametersProp);

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(exprParamsAsset);
            AssetDatabase.SaveAssets();
        }

        private void AddVrcOnlyParamToController(AnimatorController controller, string paramName, int vrcValueType, float defaultValue)
        {
            if (controller == null || string.IsNullOrEmpty(paramName)) return;

            var parameters = controller.parameters;
            foreach (var parameter in parameters)
                if (parameter.name == paramName) return;
            var vrcNames = GetVrcParameterNames(_vrcExpressionParametersAsset);
            int insertIndex = 0;
            foreach (var row in BuildParameterOrder(parameters, vrcNames))
            {
                if (row.animatorIndex >= 0) insertIndex++;
                else if (vrcNames[row.vrcIndex] == paramName) break;
            }

            AnimatorControllerParameterType animType = AnimatorControllerParameterType.Float;
            if (vrcValueType == 0) animType = AnimatorControllerParameterType.Int;
            else if (vrcValueType == 2) animType = AnimatorControllerParameterType.Bool;

            var newItem = new AnimatorControllerParameter { name = paramName, type = animType };

            if (animType == AnimatorControllerParameterType.Float) newItem.defaultFloat = defaultValue;
            else if (animType == AnimatorControllerParameterType.Int) newItem.defaultInt = Mathf.RoundToInt(defaultValue);
            else if (animType == AnimatorControllerParameterType.Bool) newItem.defaultBool = defaultValue > 0.5f;

            Undo.RecordObject(controller, "Add VRC Parameter to Animator Controller");
            var list = new List<AnimatorControllerParameter>(parameters);
            list.Insert(insertIndex, newItem);
            controller.parameters = list.ToArray();
            _selectedParamIndex = insertIndex;
            EditorUtility.SetDirty(controller);
            _parameterListControllerVersion = -1;
            AssetDatabase.SaveAssets();
            Repaint();
        }

        private void RemoveParameterFromVRC(string paramName)
        {
            if (_vrcExpressionParametersAsset == null) return;

            Undo.RegisterCompleteObjectUndo(_vrcExpressionParametersAsset, "Remove Parameter from VRC Expression Parameters");

            if (_vrcExpressionParametersAsset.parameters != null)
            {
                var list = new List<VRCExpressionParameters.Parameter>(_vrcExpressionParametersAsset.parameters);
                int removedCount = list.RemoveAll(p => p != null && p.name == paramName);

                if (removedCount > 0)
                {
                    _vrcExpressionParametersAsset.parameters = list.ToArray();
                    EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                }
            }
        }

        private bool IsAnimatorOnlyParameter(string paramName)
        {
            return _hideVrcBuiltInParameters &&
                (paramName == "BlendTreeWeight" ||
                 (!string.IsNullOrEmpty(paramName) && VrcBuiltInParameterNames.Contains(paramName)));
        }

        private void RemoveAnimatorOnlyParametersFromVrc()
        {
            if (_vrcExpressionParametersAsset == null || _vrcExpressionParametersAsset.parameters == null) return;

            var remainingParameters = new List<VRCExpressionParameters.Parameter>();
            bool removedAnimatorOnlyParameters = false;
            foreach (var parameter in _vrcExpressionParametersAsset.parameters)
            {
                if (parameter != null && IsAnimatorOnlyParameter(parameter.name))
                {
                    removedAnimatorOnlyParameters = true;
                    continue;
                }

                remainingParameters.Add(parameter);
            }

            if (!removedAnimatorOnlyParameters) return;

            Undo.RegisterCompleteObjectUndo(_vrcExpressionParametersAsset, "Remove Animator-Only Parameters from VRC Expression Parameters");
            _vrcExpressionParametersAsset.parameters = remainingParameters.ToArray();
            EditorUtility.SetDirty(_vrcExpressionParametersAsset);
            AssetDatabase.SaveAssets();
        }

        private bool TryGetVrcParamElement(string paramName, out SerializedProperty elementProp, out SerializedObject soOut)
        {
            elementProp = null;
            soOut = null;
            if (_vrcExpressionParametersAsset == null) return false;

            SerializedObject so = new SerializedObject(_vrcExpressionParametersAsset);
            SerializedProperty parametersProp = so.FindProperty("parameters");
            if (parametersProp == null || !parametersProp.isArray) return false;

            for (int i = 0; i < parametersProp.arraySize; i++)
            {
                SerializedProperty element = parametersProp.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative("name").stringValue == paramName)
                {
                    elementProp = element;
                    soOut = so;
                    return true;
                }
            }
            return false;
        }

        private HashSet<string> _cachedBlendTreeParams = new HashSet<string>();
        private readonly Dictionary<UnityEngine.Object, int> _blendParameterVersions = new Dictionary<UnityEngine.Object, int>();
        private double _lastParamCacheTime = 0;
        private AnimatorController _paramCacheController;
        private bool _hasParamCache;

        private struct ParameterListRow
        {
            public int animatorIndex;
            public int vrcIndex;
        }

        private static string[] GetVrcParameterNames(VRCExpressionParameters asset)
        {
            var parameters = asset != null ? asset.parameters : null;
            if (parameters == null) return Array.Empty<string>();
            var names = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++) names[i] = parameters[i]?.name;
            return names;
        }

        private static List<ParameterListRow> BuildParameterOrder(AnimatorControllerParameter[] animatorParameters, string[] vrcNames)
        {
            var animatorIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            var matchingVrcIndices = new int[animatorParameters.Length];
            for (int i = 0; i < animatorParameters.Length; i++)
            {
                string name = animatorParameters[i].name;
                if (!string.IsNullOrEmpty(name) && !animatorIndices.ContainsKey(name)) animatorIndices.Add(name, i);
                matchingVrcIndices[i] = -1;
            }

            // Animator order is authoritative. Keep each run of VRC-only parameters after
            // its preceding shared name, or before the next shared name for a leading run.
            var insertions = new List<int>[animatorParameters.Length + 1];
            var pending = new List<int>();
            int previousAnimatorIndex = -1;
            void PlacePending(int insertionIndex)
            {
                if (pending.Count == 0) return;
                if (insertions[insertionIndex] == null) insertions[insertionIndex] = new List<int>();
                insertions[insertionIndex].AddRange(pending);
                pending.Clear();
            }
            for (int i = 0; i < vrcNames.Length; i++)
            {
                if (!string.IsNullOrEmpty(vrcNames[i]) && animatorIndices.TryGetValue(vrcNames[i], out int animatorIndex))
                {
                    PlacePending(previousAnimatorIndex >= 0 ? previousAnimatorIndex + 1 : animatorIndex);
                    if (matchingVrcIndices[animatorIndex] < 0) matchingVrcIndices[animatorIndex] = i;
                    previousAnimatorIndex = animatorIndex;
                }
                else pending.Add(i);
            }
            PlacePending(previousAnimatorIndex >= 0 ? previousAnimatorIndex + 1 : animatorParameters.Length);

            var rows = new List<ParameterListRow>(animatorParameters.Length + vrcNames.Length);
            for (int i = 0; i <= animatorParameters.Length; i++)
            {
                if (insertions[i] != null)
                    foreach (int vrcIndex in insertions[i]) rows.Add(new ParameterListRow { animatorIndex = -1, vrcIndex = vrcIndex });
                if (i < animatorParameters.Length)
                    rows.Add(new ParameterListRow { animatorIndex = i, vrcIndex = matchingVrcIndices[i] });
            }
            return rows;
        }

        private struct ParameterDefaultMismatch
        {
            public int animatorIndex;
            public int vrcIndex;
            public double animatorDefault;
            public float vrcDefault;
            public bool canCopyToAnimator;
            public bool canCopyToVrc;
        }

        private sealed class VrcParameterDuplicateEntry
        {
            public int index;
            public VRCExpressionParameters.Parameter snapshot;
            public GUIContent nameLabel;
            public GUIContent defaultLabel;
        }

        private sealed class VrcParameterDuplicateGroup
        {
            public string name;
            public string animatorType;
            public readonly List<VrcParameterDuplicateEntry> entries = new List<VrcParameterDuplicateEntry>();
        }

        [NonSerialized] private List<VrcParameterDuplicateGroup> _parameterDuplicateGroups = new List<VrcParameterDuplicateGroup>();
        private int _parameterDefaultMismatchCount;
        private bool _canCopyVrcDefaultsToAnimator;
        private bool _canCopyAnimatorDefaultsToVrc;
        [NonSerialized] private bool _parameterListInitialized;
        private AnimatorController _parameterListController;
        private VRCExpressionParameters _parameterListVrcAsset;
        private VRCExpressionParameters _parameterSerializedAsset;
        private SerializedObject _parameterSerializedObject;
        private int _parameterListControllerVersion = -1;
        private int _parameterListVrcVersion = -1;
        private bool _parameterListExcludesInternal;
        [NonSerialized] private AnimatorControllerParameter[] _parameterListParameters = Array.Empty<AnimatorControllerParameter>();
        private readonly Dictionary<string, int> _parameterVrcIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<int> _parameterVrcOnlyIndices = new List<int>();
        [NonSerialized] private List<ParameterListRow> _parameterOrderedRows = new List<ParameterListRow>();
        private readonly List<ParameterListRow> _parameterRows = new List<ParameterListRow>();
        private string _parameterRowsQuery;
        private string _parameterRowsTypePopupId;
        private int _parameterRowsVersion;
        private int _parameterRenderVersion = -1;
        private readonly List<int> _parameterRenderedRows = new List<int>();
        private int _parameterPinnedRows;
        private int _parameterPinnedKeyboardControl;
        private int _parameterPinnedHotControl;
        private float _parameterContentWidth;
        private Rect _parameterViewportRect;
        private static readonly string[] ParameterTypeLabels = { "Bool", "Float", "Int" };
        private static readonly int[] AnimatorParameterTypeValues = { (int)AnimatorControllerParameterType.Bool, (int)AnimatorControllerParameterType.Float, (int)AnimatorControllerParameterType.Int };
        private static readonly string[] TriggerParameterTypeLabels = { "Bool", "Float", "Int", "Trigger" };
        private static readonly int[] TriggerParameterTypeValues = { (int)AnimatorControllerParameterType.Bool, (int)AnimatorControllerParameterType.Float, (int)AnimatorControllerParameterType.Int, (int)AnimatorControllerParameterType.Trigger };
        private static readonly int[] VrcParameterTypeValues = { 2, 1, 0 };

        private static double GetAnimatorParameterDefault(AnimatorControllerParameter parameter)
        {
            if (parameter.type == AnimatorControllerParameterType.Bool) return parameter.defaultBool ? 1d : 0d;
            if (parameter.type == AnimatorControllerParameterType.Int) return parameter.defaultInt;
            return parameter.defaultFloat;
        }

        private static bool UsesSeparateVrcIntDefault(AnimatorControllerParameterType animatorType, VRCExpressionParameters.ValueType vrcType)
        {
            return animatorType == AnimatorControllerParameterType.Float && vrcType == VRCExpressionParameters.ValueType.Int;
        }

        private static int GetVrcIntDefault(float value)
        {
            // Match the VRC parameter editor's Int display.
            return float.IsNaN(value) ? 0 : Mathf.FloorToInt(Mathf.Clamp(value, 0f, 255f));
        }

        private static float ClampParameterFloatDefault(float value)
        {
            return float.IsNaN(value) ? 0f : Mathf.Clamp(value, -1f, 1f);
        }

        private static double ConvertParameterDefault(double value, AnimatorControllerParameterType oldType,
            AnimatorControllerParameterType newType)
        {
            if (double.IsNaN(value) || oldType == AnimatorControllerParameterType.Trigger) value = 0d;
            if (oldType == AnimatorControllerParameterType.Bool) value = value > 0.5d ? 1d : 0d;
            if (newType == AnimatorControllerParameterType.Float) return ClampParameterFloatDefault((float)value);
            if (newType == AnimatorControllerParameterType.Bool ||
                (oldType == AnimatorControllerParameterType.Float && newType == AnimatorControllerParameterType.Int))
                return value > 0d ? 1d : 0d;
            if (newType == AnimatorControllerParameterType.Int)
                return Math.Truncate(Math.Max(int.MinValue, Math.Min(int.MaxValue, value)));
            return 0d;
        }

        private static AnimatorControllerParameterType GetAnimatorTypeForVrcType(int type)
        {
            if (type == 0) return AnimatorControllerParameterType.Int;
            if (type == 2) return AnimatorControllerParameterType.Bool;
            return AnimatorControllerParameterType.Float;
        }

        private static void ConvertVrcParameterTypeAndDefault(SerializedProperty parameter, int newType)
        {
            var typeProperty = parameter.FindPropertyRelative("valueType");
            if (typeProperty.intValue == newType) return;
            var defaultProperty = parameter.FindPropertyRelative("defaultValue");
            defaultProperty.floatValue = (float)ConvertParameterDefault(defaultProperty.floatValue,
                GetAnimatorTypeForVrcType(typeProperty.intValue), GetAnimatorTypeForVrcType(newType));
            typeProperty.intValue = newType;
        }

        private static bool CanStoreAnimatorDefault(double value, AnimatorControllerParameterType type)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return false;
            if (type == AnimatorControllerParameterType.Bool) return value == 0d || value == 1d;
            if (type == AnimatorControllerParameterType.Int)
                return value >= int.MinValue && value <= int.MaxValue && Math.Truncate(value) == value;
            return type == AnimatorControllerParameterType.Float && (double)(float)value == value;
        }

        private static bool CanStoreVrcDefault(double value, VRCExpressionParameters.ValueType type)
        {
            // Defaults are serialized as floats, even for Bool and Int. Never silently round a copied value.
            if ((double)(float)value != value) return false;
            if (type == VRCExpressionParameters.ValueType.Bool)
                return CanStoreAnimatorDefault(value, AnimatorControllerParameterType.Bool);
            if (type == VRCExpressionParameters.ValueType.Int)
                return CanStoreAnimatorDefault(value, AnimatorControllerParameterType.Int);
            return type == VRCExpressionParameters.ValueType.Float && CanStoreAnimatorDefault(value, AnimatorControllerParameterType.Float);
        }

        private List<ParameterDefaultMismatch> FindParameterDefaultMismatches(AnimatorControllerParameter[] animatorParameters,
            VRCExpressionParameters.Parameter[] vrcParameters)
        {
            var mismatches = new List<ParameterDefaultMismatch>();
            if (vrcParameters == null) return mismatches;
            var vrcIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < vrcParameters.Length; i++)
                if (vrcParameters[i] != null && !string.IsNullOrEmpty(vrcParameters[i].name))
                    vrcIndices[vrcParameters[i].name] = i;
            for (int i = 0; i < animatorParameters.Length; i++)
            {
                var parameter = animatorParameters[i];
                if (parameter.type == AnimatorControllerParameterType.Trigger || IsAnimatorOnlyParameter(parameter.name) ||
                    !vrcIndices.TryGetValue(parameter.name, out int vrcIndex)) continue;
                var vrcParameter = vrcParameters[vrcIndex];
                // These types can intentionally have different defaults. The row edits the VRC Int
                // default independently, and neither bulk sync direction should overwrite the pair.
                if (UsesSeparateVrcIntDefault(parameter.type, vrcParameter.valueType)) continue;
                double animatorDefault = GetAnimatorParameterDefault(parameter);
                if (animatorDefault == vrcParameter.defaultValue) continue;
                mismatches.Add(new ParameterDefaultMismatch
                {
                    animatorIndex = i,
                    vrcIndex = vrcIndex,
                    animatorDefault = animatorDefault,
                    vrcDefault = vrcParameter.defaultValue,
                    canCopyToAnimator = CanStoreAnimatorDefault(vrcParameter.defaultValue, parameter.type),
                    canCopyToVrc = CanStoreVrcDefault(animatorDefault, vrcParameter.valueType)
                });
            }
            return mismatches;
        }

        private static List<VrcParameterDuplicateGroup> FindVrcParameterDuplicates(
            AnimatorControllerParameter[] animatorParameters, VRCExpressionParameters.Parameter[] vrcParameters)
        {
            var groups = new List<VrcParameterDuplicateGroup>();
            if (vrcParameters == null) return groups;
            var byName = new Dictionary<string, VrcParameterDuplicateGroup>(StringComparer.Ordinal);
            for (int i = 0; i < vrcParameters.Length; i++)
            {
                var parameter = vrcParameters[i];
                if (parameter == null || string.IsNullOrEmpty(parameter.name)) continue;
                if (!byName.TryGetValue(parameter.name, out var group))
                {
                    group = new VrcParameterDuplicateGroup { name = parameter.name };
                    byName.Add(parameter.name, group);
                    groups.Add(group);
                }
                group.entries.Add(new VrcParameterDuplicateEntry { index = i });
            }
            groups.RemoveAll(group => group.entries.Count < 2);
            var animatorTypes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var parameter in animatorParameters)
            {
                if (parameter == null || string.IsNullOrEmpty(parameter.name) || !byName.TryGetValue(parameter.name, out var group) || group.entries.Count < 2) continue;
                if (!animatorTypes.TryGetValue(parameter.name, out var types))
                    animatorTypes[parameter.name] = types = new List<string>();
                string type = parameter.type.ToString();
                if (!types.Contains(type)) types.Add(type);
            }
            foreach (var group in groups)
            {
                group.animatorType = animatorTypes.TryGetValue(group.name, out var types) ? string.Join(", ", types) : "Not in Animator";
                foreach (var entry in group.entries)
                {
                    var parameter = vrcParameters[entry.index];
                    entry.snapshot = CopyVrcMergeParameter(parameter);
                    string defaultValue = parameter.defaultValue.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
                    string details = "VRC #" + (entry.index + 1) + "   " + parameter.valueType +
                        "   Default: " + defaultValue +
                        "   Saved: " + (parameter.saved ? "Yes" : "No") + "   Synced: " + (parameter.networkSynced ? "Yes" : "No");
                    entry.nameLabel = new GUIContent(parameter.name, details);
                    entry.defaultLabel = new GUIContent(defaultValue, "VRC default (read-only).\n" + details);
                }
            }
            return groups;
        }

        private void DrawVrcParameterDuplicateResolution()
        {
            var asset = _vrcExpressionParametersAsset;
            var groups = _parameterDuplicateGroups;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.HelpBox(groups.Count + " parameter name appear more than once in VRC Parameter list. " +
                "Use Auto Fix or choose a copy to keep below.", MessageType.Warning);
            if (GUILayout.Button(new GUIContent("Auto Fix",
                "Removes Dup Parameters that are not used in vrc menus."), GUILayout.Width(65)))
                EditorApplication.delayCall += () => { if (this != null) AutoFixVrcParameterDuplicates(asset); };
            EditorGUILayout.EndHorizontal();
            bool showWarning = EditorGUILayout.ToggleLeft(new GUIContent("Duplicate Warning",
                "Saves for this Unity project, you can turn it back on in Options."),
                _showParameterDuplicateWarning);
            if (showWarning != _showParameterDuplicateWarning)
            {
                GUI.FocusControl(null);
                EditorApplication.delayCall += () => SetParameterDuplicateWarning(showWarning);
            }
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(83);
            GUILayout.Label("Name", EditorStyles.boldLabel, GUILayout.MinWidth(60), GUILayout.ExpandWidth(true));
            GUILayout.Label("Animator", EditorStyles.boldLabel, GUILayout.Width(70));
            GUILayout.Space(4);
            GUILayout.Label("Default", EditorStyles.boldLabel, GUILayout.Width(55));
            GUILayout.Label("VRC Type", EditorStyles.boldLabel, GUILayout.Width(61));
            GUILayout.Space(4);
            GUILayout.Space(75);
            EditorGUILayout.EndHorizontal();
            foreach (var group in groups)
            {
                foreach (var entry in group.entries)
                {
                    var parameter = entry.snapshot;
                    EditorGUILayout.BeginHorizontal(GUILayout.Height(ElementHeight));
                    Color originalColor = GUI.color;
                    GUI.color = new Color(0.1f, 0.7f, 1f, 1f);
                    GUILayout.Label("        VRC #" + (entry.index + 1), EditorStyles.miniLabel, GUILayout.Width(83));
                    GUI.color = originalColor;
                    GUILayout.Label(entry.nameLabel, EditorStyles.textField, GUILayout.MinWidth(60), GUILayout.ExpandWidth(true));
                    GUILayout.Label(new GUIContent(group.animatorType == "Not in Animator" ? "—" : group.animatorType,
                        "Animator type: " + group.animatorType), EditorStyles.miniButton, GUILayout.Width(70));
                    GUILayout.Space(4);
                    if (parameter.valueType == VRCExpressionParameters.ValueType.Bool &&
                        (parameter.defaultValue == 0f || parameter.defaultValue == 1f))
                    {
                        GUILayout.Space(14);
                        using (new EditorGUI.DisabledScope(true))
                            EditorGUILayout.Toggle(parameter.defaultValue == 1f, GUILayout.Width(41));
                        GUI.Label(GUILayoutUtility.GetLastRect(), new GUIContent("", entry.defaultLabel.tooltip), GUIStyle.none);
                    }
                    else
                    {
                        GUILayout.Label(entry.defaultLabel, EditorStyles.textField, GUILayout.Width(51));
                        GUILayout.Space(4);
                    }
                    GUILayout.Label(new GUIContent(parameter.valueType.ToString(), "Type of this VRC entry."), EditorStyles.miniButton, GUILayout.Width(61));
                    GUILayout.Space(4);
                    if (GUILayout.Button(new GUIContent("Keep this", "Keep this VRC entry and remove the other copies of '" + group.name + "'. Supports Undo."), GUILayout.Width(75)))
                        EditorApplication.delayCall += () =>
                        {
                            if (this != null) RemoveVrcParameterDuplicates(asset, new List<VrcParameterDuplicateGroup> { group },
                                new Dictionary<string, int>(StringComparer.Ordinal) { { group.name, entry.index } });
                        };
                    EditorGUILayout.EndHorizontal();
                }
            }
        }

        private void AutoFixVrcParameterDuplicates(VRCExpressionParameters asset)
        {
            if (asset == null || asset != _vrcExpressionParametersAsset) return;
            var controller = GetAnimatorController();
            var groups = FindVrcParameterDuplicates(controller != null ? controller.parameters : Array.Empty<AnimatorControllerParameter>(), asset.parameters);
            if (groups.Count == 0)
            {
                _parameterListVrcVersion = -1;
                ShowNotification(new GUIContent("No duplicate VRC parameters remain."));
                Repaint();
                return;
            }
            var keepIndices = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var group in groups) keepIndices.Add(group.name, group.entries[0].index);
            RemoveVrcParameterDuplicates(asset, groups, keepIndices, " Kept the top copy of each name.");
        }

        private void RemoveVrcParameterDuplicates(VRCExpressionParameters asset, List<VrcParameterDuplicateGroup> groups,
            Dictionary<string, int> keepIndices, string resultNote = "")
        {
            if (asset == null || asset != _vrcExpressionParametersAsset || groups.Count == 0) return;
            var parameters = asset.parameters;
            if (parameters == null) return;
            foreach (var group in groups)
            {
                int matched = 0;
                bool changed = false;
                for (int i = 0; i < parameters.Length; i++)
                {
                    var parameter = parameters[i];
                    if (parameter == null || parameter.name != group.name) continue;
                    if (matched >= group.entries.Count) { changed = true; break; }
                    var entry = group.entries[matched++];
                    var before = entry.snapshot;
                    if (entry.index != i || parameter.valueType != before.valueType || !parameter.defaultValue.Equals(before.defaultValue) ||
                        parameter.saved != before.saved || parameter.networkSynced != before.networkSynced)
                    { changed = true; break; }
                }
                // Never remove a different entry if the asset changed after the choice was displayed.
                if (changed || matched != group.entries.Count || !keepIndices.TryGetValue(group.name, out int selectedIndex) ||
                    !group.entries.Exists(entry => entry.index == selectedIndex))
                {
                    _parameterListVrcVersion = -1;
                    CloseHubDropdown();
                    ShowNotification(new GUIContent("The VRC list changed. Review the refreshed duplicate entries."));
                    Repaint();
                    return;
                }
            }
            var remaining = new List<VRCExpressionParameters.Parameter>(parameters.Length);
            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (parameter != null && parameter.name != null && keepIndices.TryGetValue(parameter.name, out int keepIndex) && i != keepIndex) continue;
                remaining.Add(parameter);
            }
            int removed = parameters.Length - remaining.Count;
            if (removed == 0) return;
            Undo.RegisterCompleteObjectUndo(asset, "Remove Duplicate VRC Parameters");
            asset.parameters = remaining.ToArray();
            EditorUtility.SetDirty(asset);
            _parameterListControllerVersion = _parameterListVrcVersion = -1;
            _parameterUsageScannedName = null;
            _parameterMergePlan = null;
            if (_hasSetupScan) _setupChecksStale = true;
            CloseHubDropdown();
            AssetDatabase.SaveAssets();
            ShowNotification(new GUIContent("Removed " + removed + " duplicate VRC parameters." + resultNote));
            Repaint();
        }

        private void DrawParameterDefaultResolution(AnimatorController controller)
        {
            var vrcAsset = _vrcExpressionParametersAsset;
            bool hasDuplicates = _parameterDuplicateGroups.Count > 0;
            EditorGUILayout.HelpBox(_parameterDefaultMismatchCount + " VRC parameter defaults differ from the Animator defaults. " +
                (hasDuplicates ? (_showParameterDuplicateWarning ? "Remove the duplicate parameters above before matching defaults." :
                "Resolve duplicate VRC parameters before matching defaults (enable Duplicate Warning in Options).") :
                "Choose which defaults to keep."), MessageType.Warning);
            bool showWarning = EditorGUILayout.ToggleLeft(new GUIContent("Defaults Warning",
                "Saves for this Unity project, you can turn it back on in Options."),
                _showParameterDefaultWarning);
            if (showWarning != _showParameterDefaultWarning)
            {
                GUI.FocusControl(null);
                EditorApplication.delayCall += () => SetParameterDefaultWarning(showWarning);
            }
            bool sideBySide = position.width >= 650f;
            if (sideBySide) EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(hasDuplicates || !_canCopyAnimatorDefaultsToVrc))
            {
                if (GUILayout.Button(new GUIContent("Make VRC Parameters match Animator",
                    "Keep the Animator defaults and copy them to VRC Parameters.")))
                    EditorApplication.delayCall += () => MatchParameterDefaults(controller, vrcAsset, false);
            }
            using (new EditorGUI.DisabledScope(hasDuplicates || !_canCopyVrcDefaultsToAnimator))
            {
                if (GUILayout.Button(new GUIContent("Make Animator match VRC Parameters",
                    "Keep the VRC defaults and copy them to Animator.")))
                    EditorApplication.delayCall += () => MatchParameterDefaults(controller, vrcAsset, true);
            }
            if (sideBySide) EditorGUILayout.EndHorizontal();
            if (!hasDuplicates && (!_canCopyAnimatorDefaultsToVrc || !_canCopyVrcDefaultsToAnimator))
                EditorGUILayout.HelpBox("A disabled option would require rounding or changing a parameter type. " +
                    "Use the other direction, or correct the types and defaults in the Animator or VRC Parameters asset.", MessageType.Info);
        }

        private void MatchParameterDefaults(AnimatorController controller, VRCExpressionParameters vrcAsset, bool copyFromVrc)
        {
            if (controller == null || vrcAsset == null) return;
            var animatorParameters = controller.parameters;
            var vrcParameters = vrcAsset.parameters;
            if (FindVrcParameterDuplicates(animatorParameters, vrcParameters).Count > 0)
            {
                _parameterListVrcVersion = -1;
                Repaint();
                return;
            }
            // Resolve current names and values after drawing, without retaining SerializedProperties.
            var mismatches = FindParameterDefaultMismatches(animatorParameters, vrcParameters);
            if (mismatches.Count == 0) return;
            foreach (var mismatch in mismatches)
                if (copyFromVrc ? !mismatch.canCopyToAnimator : !mismatch.canCopyToVrc) return;
            Undo.RegisterCompleteObjectUndo(copyFromVrc ? (UnityEngine.Object)controller : vrcAsset,
                copyFromVrc ? "Match Animator Defaults to VRC" : "Match VRC Defaults to Animator");
            foreach (var mismatch in mismatches)
            {
                if (copyFromVrc)
                {
                    var parameter = animatorParameters[mismatch.animatorIndex];
                    if (parameter.type == AnimatorControllerParameterType.Bool) parameter.defaultBool = mismatch.vrcDefault == 1f;
                    else if (parameter.type == AnimatorControllerParameterType.Int) parameter.defaultInt = (int)mismatch.vrcDefault;
                    else parameter.defaultFloat = mismatch.vrcDefault;
                }
                else vrcParameters[mismatch.vrcIndex].defaultValue = (float)mismatch.animatorDefault;
            }
            if (copyFromVrc)
            {
                controller.parameters = animatorParameters;
                EditorUtility.SetDirty(controller);
            }
            else
            {
                vrcAsset.parameters = vrcParameters;
                EditorUtility.SetDirty(vrcAsset);
            }
            _parameterListControllerVersion = _parameterListVrcVersion = -1;
            AssetDatabase.SaveAssets();
            Repaint();
        }

        private bool BlendParameterCacheChanged()
        {
            foreach (var version in _blendParameterVersions)
                if (version.Key == null || EditorUtility.GetDirtyCount(version.Key) != version.Value) return true;
            return false;
        }

        private SerializedObject GetParameterSerializedObject()
        {
            if (_parameterSerializedAsset != _vrcExpressionParametersAsset ||
                (_parameterSerializedObject == null && _vrcExpressionParametersAsset != null) ||
                (_parameterSerializedObject != null && _vrcExpressionParametersAsset == null))
            {
                _parameterSerializedObject?.Dispose();
                _parameterSerializedAsset = _vrcExpressionParametersAsset;
                _parameterSerializedObject = _parameterSerializedAsset != null ? new SerializedObject(_parameterSerializedAsset) : null;
            }
            _parameterSerializedObject?.UpdateIfRequiredOrScript();
            return _parameterSerializedObject;
        }

        private void RefreshParameterList(AnimatorController controller)
        {
            // Keep one data/layout snapshot for the entire IMGUI event sequence.
            if (_parameterListInitialized && Event.current.type != EventType.Layout && _parameterListController == controller &&
                _parameterListVrcAsset == _vrcExpressionParametersAsset) return;
            int controllerVersion = EditorUtility.GetDirtyCount(controller);
            int vrcVersion = _vrcExpressionParametersAsset != null ? EditorUtility.GetDirtyCount(_vrcExpressionParametersAsset) : -1;
            bool contextChanged = !_parameterListInitialized || _parameterListController != controller || _parameterListVrcAsset != _vrcExpressionParametersAsset;
            bool dataChanged = contextChanged ||
                _parameterListControllerVersion != controllerVersion || _parameterListVrcVersion != vrcVersion ||
                _parameterListExcludesInternal != _hideVrcBuiltInParameters;
            if (dataChanged)
            {
                bool hadDuplicates = _parameterDuplicateGroups.Count > 0;
                var vrcParameters = _vrcExpressionParametersAsset != null ? _vrcExpressionParametersAsset.parameters : null;
                _parameterDuplicateGroups = FindVrcParameterDuplicates(controller.parameters, vrcParameters);
                if (_hideVrcBuiltInParameters && _parameterDuplicateGroups.Count == 0)
                {
                    RemoveAnimatorOnlyParametersFromVrc();
                    EnsureBlendTreeWeightFloatParameter(controller);
                }
                _parameterListController = controller;
                _parameterListVrcAsset = _vrcExpressionParametersAsset;
                _parameterListParameters = controller.parameters;
                _parameterListControllerVersion = EditorUtility.GetDirtyCount(controller);
                _parameterListVrcVersion = _vrcExpressionParametersAsset != null ? EditorUtility.GetDirtyCount(_vrcExpressionParametersAsset) : -1;
                _parameterListExcludesInternal = _hideVrcBuiltInParameters;
                if (_parameterSerializedObject != null && _vrcExpressionParametersAsset != null &&
                    _parameterSerializedAsset == _vrcExpressionParametersAsset)
                    _parameterSerializedObject.Update();
                var animatorNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameter in _parameterListParameters) animatorNames.Add(parameter.name);
                _parameterVrcIndices.Clear();
                _parameterVrcOnlyIndices.Clear();
                vrcParameters = _vrcExpressionParametersAsset != null ? _vrcExpressionParametersAsset.parameters : null;
                if (vrcParameters != null)
                    for (int i = 0; i < vrcParameters.Length; i++)
                    {
                        var parameter = vrcParameters[i];
                        if (parameter == null || string.IsNullOrEmpty(parameter.name) || IsAnimatorOnlyParameter(parameter.name)) continue;
                        _parameterVrcIndices[parameter.name] = i;
                        if (!animatorNames.Contains(parameter.name)) _parameterVrcOnlyIndices.Add(i);
                    }
                _parameterOrderedRows = BuildParameterOrder(_parameterListParameters, GetVrcParameterNames(_vrcExpressionParametersAsset));
                var defaultMismatches = FindParameterDefaultMismatches(_parameterListParameters, vrcParameters);
                bool resolutionVisibilityChanged = (_showParameterDuplicateWarning && (hadDuplicates != (_parameterDuplicateGroups.Count > 0))) ||
                    (_showParameterDefaultWarning && ((_parameterDefaultMismatchCount > 0) != (defaultMismatches.Count > 0)));
                _parameterDefaultMismatchCount = defaultMismatches.Count;
                _canCopyVrcDefaultsToAnimator = _canCopyAnimatorDefaultsToVrc = true;
                foreach (var mismatch in defaultMismatches)
                {
                    _canCopyVrcDefaultsToAnimator &= mismatch.canCopyToAnimator;
                    _canCopyAnimatorDefaultsToVrc &= mismatch.canCopyToVrc;
                }
                if (resolutionVisibilityChanged)
                {
                    _paramScrollPos = Vector2.zero;
                    _parameterRenderVersion = -1;
                    ResetParameterDrag();
                    GUI.FocusControl(null);
                    Repaint();
                }
            }
            string typePopupId = _hubDropdownOwnerId ?? _hubDropdownValueId;
            if (typePopupId != null && !typePopupId.StartsWith("AnimatorParameterType_", StringComparison.Ordinal) &&
                !typePopupId.StartsWith("VrcParameterType_", StringComparison.Ordinal)) typePopupId = null;
            if (!dataChanged && _parameterRowsQuery == _paramSearchQuery && _parameterRowsTypePopupId == typePopupId) return;
            _parameterRowsQuery = _paramSearchQuery;
            _parameterRowsTypePopupId = typePopupId;
            var rows = new List<ParameterListRow>();
            foreach (var row in _parameterOrderedRows)
            {
                string name = row.animatorIndex >= 0 ? _parameterListParameters[row.animatorIndex].name :
                    _vrcExpressionParametersAsset.parameters[row.vrcIndex]?.name;
                if (row.animatorIndex < 0 && (string.IsNullOrEmpty(name) || IsAnimatorOnlyParameter(name))) continue;
                // Keep a renamed row available until its open type menu has applied
                // the selection, even if the new name no longer matches the search.
                bool ownsTypePopup = typePopupId != null &&
                    ((row.animatorIndex >= 0 && typePopupId == "AnimatorParameterType_" + controller.GetInstanceID() + "_" + row.animatorIndex) ||
                     (row.vrcIndex >= 0 && _vrcExpressionParametersAsset != null &&
                      typePopupId == "VrcParameterType_" + _vrcExpressionParametersAsset.GetInstanceID() + "_" + row.vrcIndex));
                if (ownsTypePopup || string.IsNullOrEmpty(_parameterRowsQuery) || name.IndexOf(_parameterRowsQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                    rows.Add(row);
            }
            bool rowsChanged = contextChanged || rows.Count != _parameterRows.Count;
            for (int i = 0; !rowsChanged && i < rows.Count; i++)
                rowsChanged = rows[i].animatorIndex != _parameterRows[i].animatorIndex || rows[i].vrcIndex != _parameterRows[i].vrcIndex;
            if (rowsChanged)
            {
                _parameterRows.Clear();
                _parameterRows.AddRange(rows);
                _parameterRowsVersion++;
            }
            // Only mark the snapshot ready after both the source parameters and all row lookups are rebuilt.
            _parameterListInitialized = true;
        }

        private void UpdateParameterVisibleRows(float rowHeight)
        {
            if (Event.current.type != EventType.Layout) return;
            int count = _parameterRows.Count;
            _paramScrollPos.y = Mathf.Clamp(_paramScrollPos.y, 0f, Mathf.Max(0f, count * rowHeight - _paramListHeight));
            int first = Mathf.Max(0, Mathf.FloorToInt(_paramScrollPos.y / rowHeight) - 1);
            int last = Mathf.Min(count, Mathf.CeilToInt((_paramScrollPos.y + _paramListHeight) / rowHeight) + 1);
            bool holdControls = GUIUtility.keyboardControl != 0 || GUIUtility.hotControl != 0;
            if (_parameterRenderVersion != _parameterRowsVersion || !holdControls)
            {
                _parameterRenderedRows.Clear();
                _parameterPinnedRows = 0;
            }
            else
            {
                // Keep the original controls/IDs first while a field, popup or scrollbar
                // has focus. Newly visible rows follow them, even when scrolling far away.
                if (_parameterPinnedRows == 0 || _parameterPinnedKeyboardControl != GUIUtility.keyboardControl ||
                    (_parameterPinnedHotControl != GUIUtility.hotControl && GUIUtility.hotControl != 0))
                    _parameterPinnedRows = _parameterRenderedRows.Count;
                if (_parameterRenderedRows.Count > _parameterPinnedRows)
                    _parameterRenderedRows.RemoveRange(_parameterPinnedRows, _parameterRenderedRows.Count - _parameterPinnedRows);
            }
            for (int i = first; i < last; i++)
                if (!_parameterRenderedRows.Contains(i)) _parameterRenderedRows.Add(i);
            _parameterPinnedKeyboardControl = GUIUtility.keyboardControl;
            _parameterPinnedHotControl = GUIUtility.hotControl;
            _parameterRenderVersion = _parameterRowsVersion;
        }

        private void DrawParameterManager(AnimatorController controller)
        {
            if (!_showParamManager && _animationToolResizeIndex == ParameterUsageToolIndex) FinishAnimationToolResize();
            if (_isParamDragging && _paramDragController != controller) CancelParameterDrag();
            if (!_showParamManager) CancelParameterDrag();
            if (_showParamManager)
            {
                GUILayout.Space(5);
                EditorGUILayout.LabelField("Parameters Manager", EditorStyles.boldLabel);
                RefreshParameterList(controller);

                double currentTime = EditorApplication.timeSinceStartup;
                if (!_hasParamCache || _paramCacheController != controller ||
                    (Event.current.type == EventType.Layout && currentTime - _lastParamCacheTime > 2.0))
                {
                    if (!_hasParamCache || _paramCacheController != controller || BlendParameterCacheChanged())
                        _cachedBlendTreeParams = GetParametersUsedInBlendTrees(controller);
                    _lastParamCacheTime = currentTime;
                    _paramCacheController = controller;
                    _hasParamCache = true;
                }
                HashSet<string> blendTreeParams = _cachedBlendTreeParams;

                EditorGUILayout.BeginVertical("box");

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("+ Add Parameter", GUILayout.Width(120)))
                {
                    AddParameterToController(controller, "NewParameter", AnimatorControllerParameterType.Float);
                }

                EditorGUI.BeginChangeCheck();
                _paramAddPosition = (AddPosition)HubEnumPopup("ParameterAddPosition", null, _paramAddPosition, GUILayout.Width(130));
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetInt(PrefParamAddPositionKey, (int)_paramAddPosition);
                }

                // Keep the VRC and parameter tools together when the window is narrow.
                if (position.width < 780f)
                {
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                }

                if (HubDropdownButtonWithId("AddVrcParameter", new GUIContent("+ Add VRC Parameter ▾"), GUI.skin.button, GUILayout.Width(150)))
                {
                    HubDropdownMenu menu = new HubDropdownMenu(this);

                    foreach (var kvp in VrcBuiltInParameterTypes)
                    {
                        string paramName = kvp.Key;
                        AnimatorControllerParameterType paramType = kvp.Value;

                        bool exists = false;
                        foreach (var existingP in controller.parameters)
                        {
                            if (existingP.name == paramName) { exists = true; break; }
                        }

                        if (!exists)
                        {
                            menu.AddItem(new GUIContent(paramName), false, () => {
                                AddParameterToController(controller, paramName, paramType);
                            });
                        }
                        else
                        {
                            menu.AddDisabledItem(new GUIContent(paramName + " (Already Exists)"));
                        }
                    }

                    menu.ShowAsContext();
                }

                DrawParameterManagerTools(controller);
                DrawParameterManagerOptions();

                EditorGUILayout.EndHorizontal();

                GUILayout.Space(5);

                DrawParameterManagerMissingParameters();
                DrawUnusedParameterRemovalPreview();

                AnimatorControllerParameter[] parameters = _parameterListParameters;
                SerializedObject vrcSo = GetParameterSerializedObject();
                SerializedProperty vrcParametersProp = vrcSo != null ? vrcSo.FindProperty("parameters") : null;

                DrawParameterUsagePanel(controller);
                DrawSyncedParameterMemory();
                bool hasDefaultMismatches = _showParameterDefaultWarning && _parameterDefaultMismatchCount > 0;
                bool hasDuplicates = _showParameterDuplicateWarning && _parameterDuplicateGroups.Count > 0;
                bool needsResolution = hasDuplicates || hasDefaultMismatches;
                float parameterRowHeight = ElementHeight + EditorGUIUtility.standardVerticalSpacing;
                float totalContentHeight = _parameterRows.Count * parameterRowHeight;
                if (!needsResolution)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUI.BeginChangeCheck();
                    _paramSearchQuery = EditorGUILayout.TextField(
                        new GUIContent("Find parameter", "Search Animator and VRC parameter names."),
                        _paramSearchQuery, EditorStyles.toolbarSearchField);
                    if (EditorGUI.EndChangeCheck()) _paramScrollPos = Vector2.zero;
                    if (GUILayout.Button("Clear", GUILayout.Width(45)))
                    {
                        _paramSearchQuery = "";
                        _paramScrollPos = Vector2.zero;
                        GUI.FocusControl(null);
                    }
                    EditorGUILayout.EndHorizontal();

                    // Keep the column headings on their own row, anchored to the fields on the right.
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(new GUIContent("Default", "Editing an Animator default here also updates its matching VRC default. Float Animator / Int VRC pairs show and edit only the VRC Int default. VRC-only rows edit the VRC default."), EditorStyles.boldLabel, GUILayout.Width(55));
                    if (_vrcExpressionParametersAsset != null)
                    {
                        GUILayout.Label("VRC Type", EditorStyles.boldLabel, GUILayout.Width(65));
                        GUILayout.Label("Saved", EditorStyles.boldLabel, GUILayout.Width(45));
                        GUILayout.Label("Synced", EditorStyles.boldLabel, GUILayout.Width(50));
                        GUILayout.Space(75);
                    }
                    bool scrollbarVisible = totalContentHeight > _paramListHeight;
                    if (!scrollbarVisible) GUILayout.Space(-12);
                    GUILayout.Space(40);
                    EditorGUILayout.EndHorizontal();
                }

                Event e = Event.current;
                Vector2 pointerBeforeParameterScroll = e.mousePosition;

                using var paramScrollView = new NestedScrollViewScope(ref _paramScrollPos, GUILayout.Height(_paramListHeight));
                Vector2 parameterPointer = e.mousePosition;
                if (!needsResolution) UpdateParameterVisibleRows(parameterRowHeight);

                int paramToDelete = -1;
                int vrcParamToRemove = -1;
                int toggleSelectIndex = -2;
                string typeChangeParamName = null;
                AnimatorControllerParameterType oldParamType = AnimatorControllerParameterType.Float;
                AnimatorControllerParameterType newParamType = AnimatorControllerParameterType.Float;
                bool hasTypeChange = false;
                bool isSearching = !string.IsNullOrEmpty(_parameterRowsQuery);

                int vrcParamToAdd = -1;
                int vrcOnlyToDelete = -1;
                int animatorParamToAddToVrc = -1;

                EditorGUI.BeginChangeCheck();
                if (needsResolution)
                {
                    if (hasDuplicates) DrawVrcParameterDuplicateResolution();
                    if (hasDefaultMismatches)
                    {
                        if (hasDuplicates) GUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
                        DrawParameterDefaultResolution(controller);
                    }
                }
                else if (parameters.Length == 0 && _parameterVrcOnlyIndices.Count == 0)
                {
                    EditorGUILayout.HelpBox("No Parameters found in controller or VRC Parameters.", MessageType.None);
                }
                else
                {
                    // One layout entry supplies the full scroll range. Only the small visible
                    // window (plus controls retaining focus) needs actual editors.
                    Rect contentRect = GUILayoutUtility.GetRect(0f, totalContentHeight, GUILayout.ExpandWidth(true));
                    if (e.type == EventType.Repaint && !Mathf.Approximately(_parameterContentWidth, contentRect.width))
                    {
                        _parameterContentWidth = contentRect.width;
                        Repaint();
                    }
                    float rowWidth = _parameterContentWidth > 0f ? _parameterContentWidth : Mathf.Max(100f, position.width - 60f);
                    foreach (int rowIndex in _parameterRenderedRows)
                    {
                        if (rowIndex < 0 || rowIndex >= _parameterRows.Count) continue;
                        var row = _parameterRows[rowIndex];
                        GUILayout.BeginArea(new Rect(0f, rowIndex * parameterRowHeight, rowWidth, ElementHeight));
                        if (row.animatorIndex >= 0)
                        {
                            int i = row.animatorIndex;
                            var param = parameters[i];

                            Color originalBgColor = GUI.backgroundColor;
                            Color originalColor = GUI.color;

                            if (_isParamDragging && _paramDragSourceIndex == i)
                            {
                                GUI.backgroundColor = new Color(0.3f, 0.7f, 1f, 1f);
                            }
                            else if (_selectedParamIndex == i)
                            {
                                GUI.backgroundColor = new Color(0.4f, 0.7f, 1f, 0.7f);
                            }

                            EditorGUILayout.BeginHorizontal(GUILayout.Height(ElementHeight));

                            if (isSearching)
                            {
                                GUI.color = new Color(1f, 1f, 1f, 0.3f);
                                GUILayout.Label(new GUIContent("≡", "Clear search to reorder parameters."), EditorStyles.boldLabel, GUILayout.Width(15));
                                GUI.color = originalColor;
                            }
                            else
                            {
                                GUILayout.Label(new GUIContent("≡", ReorderDragTooltip), EditorStyles.boldLabel, GUILayout.Width(15));
                                Rect handleRect = GUILayoutUtility.GetLastRect();
                                EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.Pan);

                                if (e.type == EventType.MouseDown && e.button == 0 && _parameterViewportRect.Contains(pointerBeforeParameterScroll) && handleRect.Contains(e.mousePosition))
                                {
                                    _paramDragSourceIndex = i;
                                    _isParamDragging = true;
                                    _paramDragController = controller;
                                    _paramDragOriginalParameters = parameters;
                                    _paramDragOriginalSelection = _selectedParamIndex;
                                    _paramDragHasMoved = false;
                                    GUI.FocusControl(null);
                                    e.Use();
                                }
                            }

                            if (GUILayout.Button(_selectedParamIndex == i ? "Selected" : "Select", GUILayout.Width(65)))
                            {
                                if (_selectedParamIndex == i) toggleSelectIndex = -1;
                                else toggleSelectIndex = i;
                                GUI.FocusControl(null);
                            }

                            string oldName = param.name;
                            GUI.SetNextControlName("AnimParam_" + i);

                            string newName = EditorGUILayout.DelayedTextField(oldName, GUILayout.ExpandWidth(true));

                            if (newName != oldName && !string.IsNullOrEmpty(newName))
                            {
                                bool nameExists = false;
                                foreach (var p in controller.parameters)
                                {
                                    if (p.name == newName && p != param)
                                    {
                                        nameExists = true;
                                        break;
                                    }
                                }

                                if (nameExists)
                                {
                                    EditorUtility.DisplayDialog("Name Conflict", $"A parameter named '{newName}' already exists.", "OK");
                                }
                                else
                                {
                                    Undo.RecordObject(controller, "Rename Parameter");
                                    parameters[i].name = newName;
                                    controller.parameters = parameters;
                                    EditorUtility.SetDirty(controller);

                                    RenameParameterEverywhere(controller, oldName, newName);
                                    if (_parameterVrcIndices.TryGetValue(oldName, out int renamedVrcIndex))
                                    {
                                        _parameterVrcIndices.Remove(oldName);
                                        _parameterVrcIndices[newName] = renamedVrcIndex;
                                        vrcSo.Update();
                                    }
                                    _parameterListControllerVersion = -1;
                                }
                            }

                            AnimatorControllerParameterType currentType = param.type;

                            string[] typeLabels = currentType == AnimatorControllerParameterType.Trigger ? TriggerParameterTypeLabels : ParameterTypeLabels;
                            int[] typeValues = currentType == AnimatorControllerParameterType.Trigger ? TriggerParameterTypeValues : AnimatorParameterTypeValues;

                            bool forceBlendTreeWeight = _hideVrcBuiltInParameters && param.name == "BlendTreeWeight";
                            EditorGUI.BeginDisabledGroup(forceBlendTreeWeight);
                            // A rename can commit while this popup is open; keep its key independent of the name.
                            int chosenTypeInt = HubIntPopup("AnimatorParameterType_" + controller.GetInstanceID() + "_" + i,
                                forceBlendTreeWeight ? (int)AnimatorControllerParameterType.Float : (int)currentType,
                                typeLabels,
                                typeValues,
                                GUILayout.Width(70));
                            EditorGUI.EndDisabledGroup();
                            AnimatorControllerParameterType chosenType = (AnimatorControllerParameterType)chosenTypeInt;

                            if (chosenType != currentType)
                            {
                                bool proceed = true;
                                if (currentType == AnimatorControllerParameterType.Float && chosenType != AnimatorControllerParameterType.Float)
                                {
                                    bool isUsedInBlendTree = blendTreeParams.Contains(param.name);
                                    if (isUsedInBlendTree)
                                    {
                                        proceed = EditorUtility.DisplayDialog(
                                            "Warning: Parameter Used in Blend Tree",
                                            $"The parameter '{param.name}' is currently used inside one or more (BlendTrees, Motion Times or Speed Multipliers).\n\nChanging its type from Float to '{chosenType}' may break the behavior.\n\nDo you want to proceed?",
                                            "Yes, Change Type",
                                            "Cancel"
                                        );
                                    }
                                }

                                if (proceed)
                                {
                                    hasTypeChange = true;
                                    typeChangeParamName = param.name;
                                    oldParamType = currentType;
                                    newParamType = chosenType;
                                }
                            }

                            bool inVrc = false;
                            SerializedProperty vrcElementProp = null;
                            SerializedObject vrcSoProp = vrcSo;

                            if (_vrcExpressionParametersAsset != null)
                            {
                                if (_parameterVrcIndices.TryGetValue(param.name, out int vrcIndex) &&
                                    vrcParametersProp != null && vrcIndex < vrcParametersProp.arraySize)
                                {
                                    vrcElementProp = vrcParametersProp.GetArrayElementAtIndex(vrcIndex);
                                    inVrc = true;
                                }
                            }

                            bool useVrcDefault = inVrc && UsesSeparateVrcIntDefault(param.type,
                                (VRCExpressionParameters.ValueType)vrcElementProp.FindPropertyRelative("valueType").intValue);
                            // Float Animator / Int VRC pairs keep independent defaults; all other
                            // Animator rows retain their normal default editing and sync behavior.
                            bool displayAsBool = param.type == AnimatorControllerParameterType.Bool;
                            bool displayAsInt = useVrcDefault || param.type == AnimatorControllerParameterType.Int;
                            double storedDefault = useVrcDefault ? vrcElementProp.FindPropertyRelative("defaultValue").floatValue : GetAnimatorParameterDefault(param);
                            float currentDefault = (float)storedDefault;
                            if (param.type != AnimatorControllerParameterType.Float && blendTreeParams.Contains(param.name))
                            {
                                GUI.color = new Color(1f, 0.4f, 0.4f);
                                GUILayout.Label(new GUIContent("⚠️", "Needs to be a Float for BlendTrees, Motion Times & Speed Multipliers"), GUILayout.Width(20));
                                GUI.color = originalColor;
                            }
                            else GUILayout.Space(24);

                            double newDefault = storedDefault;
                            bool defaultChanged = false;

                            if (forceBlendTreeWeight)
                            {
                                EditorGUI.BeginDisabledGroup(true);
                                EditorGUILayout.FloatField(1f, GUILayout.Width(51));
                                EditorGUI.EndDisabledGroup();
                                GUILayout.Space(4);
                            }
                            else if (displayAsBool)
                            {
                                GUILayout.Space(14);
                                EditorGUI.BeginChangeCheck();
                                bool bVal = EditorGUILayout.Toggle(currentDefault > 0.5f, GUILayout.Width(41));
                                if (EditorGUI.EndChangeCheck())
                                {
                                    newDefault = bVal ? 1f : 0f;
                                    defaultChanged = true;
                                }
                            }
                            else if (displayAsInt)
                            {
                                EditorGUI.BeginChangeCheck();
                                int iVal = EditorGUILayout.IntField(useVrcDefault ? GetVrcIntDefault(currentDefault) : param.defaultInt, GUILayout.Width(51));
                                if (useVrcDefault)
                                    GUI.Label(GUILayoutUtility.GetLastRect(), new GUIContent("", "VRC Int default. Editing this value leaves the Animator Float default unchanged; this pair is excluded from default syncing."), GUIStyle.none);
                                GUILayout.Space(4);
                                if (EditorGUI.EndChangeCheck())
                                {
                                    newDefault = iVal;
                                    defaultChanged = true;
                                }
                            }
                            else
                            {
                                EditorGUI.BeginChangeCheck();
                                float fVal = EditorGUILayout.FloatField(currentDefault, GUILayout.Width(51));
                                GUILayout.Space(4);
                                if (EditorGUI.EndChangeCheck())
                                {
                                    newDefault = ClampParameterFloatDefault(fVal);
                                    defaultChanged = true;
                                }
                            }

                            if (defaultChanged && useVrcDefault)
                            {
                                Undo.RecordObject(_vrcExpressionParametersAsset, "Change VRC Parameter Default");
                                vrcSoProp.Update();
                                vrcElementProp.FindPropertyRelative("defaultValue").floatValue = (float)newDefault;
                                vrcSoProp.ApplyModifiedPropertiesWithoutUndo();
                                EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                                AssetDatabase.SaveAssets();
                            }
                            else if (defaultChanged)
                            {
                                if (inVrc)
                                    Undo.RecordObjects(new UnityEngine.Object[] { controller, _vrcExpressionParametersAsset }, "Change Parameter Default");
                                else Undo.RecordObject(controller, "Change Parameter Default");
                                if (param.type == AnimatorControllerParameterType.Float) parameters[i].defaultFloat = (float)newDefault;
                                else if (param.type == AnimatorControllerParameterType.Int) parameters[i].defaultInt = (int)newDefault;
                                else if (param.type == AnimatorControllerParameterType.Bool) parameters[i].defaultBool = newDefault > 0.5f;

                                controller.parameters = parameters;
                                EditorUtility.SetDirty(controller);

                                // Sync only an edit made in this field. Existing mismatches still require a choice.
                                if (inVrc)
                                {
                                    vrcSoProp.Update();
                                    vrcElementProp.FindPropertyRelative("defaultValue").floatValue = (float)GetAnimatorParameterDefault(parameters[i]);
                                    vrcSoProp.ApplyModifiedPropertiesWithoutUndo();
                                    EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                                    AssetDatabase.SaveAssets();
                                }
                            }

                            if (_vrcExpressionParametersAsset != null)
                            {
                                if (!inVrc)
                                {
                                    GUILayout.Space(168);

                                    if (IsAnimatorOnlyParameter(param.name))
                                    {
                                        string parameterLabel = param.name == "BlendTreeWeight" ? "Animator-only" : "Built-in";
                                        GUILayout.Label(parameterLabel, EditorStyles.miniLabel, GUILayout.Width(75));
                                    }
                                    else
                                    {
                                        GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
                                        if (GUILayout.Button("+ VRC", EditorStyles.miniButton, GUILayout.Width(75)))
                                        {
                                            animatorParamToAddToVrc = i;
                                        }
                                        GUI.backgroundColor = originalBgColor;
                                    }
                                }
                                else
                                {
                                    SerializedProperty typeProp = vrcElementProp.FindPropertyRelative("valueType");
                                    EditorGUI.BeginChangeCheck();

                                    int newTypeVal = HubIntPopup("VrcParameterType_" + _vrcExpressionParametersAsset.GetInstanceID() + "_" + row.vrcIndex,
                                        typeProp.intValue, ParameterTypeLabels, VrcParameterTypeValues, GUILayout.Width(61));

                                    GUILayout.Space(4);
                                    if (EditorGUI.EndChangeCheck())
                                    {
                                        Undo.RegisterCompleteObjectUndo(_vrcExpressionParametersAsset, "Change VRC Parameter Type");
                                        vrcSoProp.Update();
                                        ConvertVrcParameterTypeAndDefault(vrcElementProp, newTypeVal);
                                        vrcSoProp.ApplyModifiedPropertiesWithoutUndo();
                                        EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                                        AssetDatabase.SaveAssets();
                                    }

                                    SerializedProperty savedProp = vrcElementProp.FindPropertyRelative("saved");
                                    GUILayout.Space(12);
                                    EditorGUI.BeginChangeCheck();
                                    bool newSaved = EditorGUILayout.Toggle(savedProp.boolValue, GUILayout.Width(33));
                                    if (EditorGUI.EndChangeCheck())
                                    {
                                        vrcSoProp.Update();
                                        savedProp.boolValue = newSaved;
                                        vrcSoProp.ApplyModifiedProperties();
                                        EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                                        AssetDatabase.SaveAssets();
                                    }

                                    SerializedProperty syncedProp = vrcElementProp.FindPropertyRelative("networkSynced");
                                    GUILayout.Space(16);
                                    if (syncedProp != null)
                                    {
                                        EditorGUI.BeginChangeCheck();
                                        bool newSynced = EditorGUILayout.Toggle(syncedProp.boolValue, GUILayout.Width(34));
                                        if (EditorGUI.EndChangeCheck())
                                        {
                                            vrcSoProp.Update();
                                            syncedProp.boolValue = newSynced;
                                            vrcSoProp.ApplyModifiedProperties();
                                            EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                                            AssetDatabase.SaveAssets();
                                        }
                                    }
                                    else
                                    {
                                        GUILayout.Space(34);
                                    }

                                    GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
                                    if (GUILayout.Button("- VRC", EditorStyles.miniButton, GUILayout.Width(75)))
                                    {
                                        vrcParamToRemove = i;
                                    }
                                    GUI.backgroundColor = originalBgColor;
                                }
                            }

                            GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                            if (GUILayout.Button("X", GUILayout.Width(22)))
                            {
                                paramToDelete = i;
                            }
                            GUI.backgroundColor = originalBgColor;

                            EditorGUILayout.EndHorizontal();
                        }
                        else
                        {
                            int vIndex = row.vrcIndex;
                            SerializedProperty vElem = vrcParametersProp.GetArrayElementAtIndex(vIndex);
                            string vName = vElem.FindPropertyRelative("name").stringValue;

                            Color originalBgColor = GUI.backgroundColor;
                            Color originalColor = GUI.color;

                            EditorGUILayout.BeginHorizontal(GUILayout.Height(ElementHeight));

                            GUI.color = new Color(0.1f, 0.7f, 1f, 1f);
                            GUILayout.Label("        VRC Only", EditorStyles.miniLabel, GUILayout.Width(83));
                            GUI.color = originalColor;

                            EditorGUI.BeginChangeCheck();
                            GUI.SetNextControlName("VrcParam_" + vIndex);

                            string newVName = EditorGUILayout.DelayedTextField(vName, GUILayout.ExpandWidth(true));

                            if (EditorGUI.EndChangeCheck() && !string.IsNullOrEmpty(newVName) && newVName != vName)
                            {
                                Undo.RegisterCompleteObjectUndo(_vrcExpressionParametersAsset, "Rename VRC Expression Parameter");
                                vElem.FindPropertyRelative("name").stringValue = newVName;
                                vrcSo.ApplyModifiedProperties();
                                EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                            }

                            GUILayout.Space(98);

                            SerializedProperty typeProp = vElem.FindPropertyRelative("valueType");
                            SerializedProperty defaultProp = vElem.FindPropertyRelative("defaultValue");
                            int vrcValueType = typeProp.intValue;
                            float curDefault = defaultProp.floatValue;
                            float newVrcDefault = curDefault;

                            EditorGUI.BeginChangeCheck();
                            if (vrcValueType == 2)
                            {
                                GUILayout.Space(14);
                                bool bVal = EditorGUILayout.Toggle(curDefault > 0.5f, GUILayout.Width(41));
                                newVrcDefault = bVal ? 1f : 0f;
                            }
                            else if (vrcValueType == 0)
                            {
                                newVrcDefault = EditorGUILayout.IntField(Mathf.RoundToInt(curDefault), GUILayout.Width(51));
                                GUILayout.Space(4);
                            }
                            else
                            {
                                newVrcDefault = ClampParameterFloatDefault(EditorGUILayout.FloatField(curDefault, GUILayout.Width(51)));
                                GUILayout.Space(4);
                            }
                            if (EditorGUI.EndChangeCheck())
                            {
                                Undo.RegisterCompleteObjectUndo(_vrcExpressionParametersAsset, "Change VRC Parameter Default");
                                // Refresh the cached SerializedObject before committing this field,
                                // then persist VRC-only defaults just like Animator-linked defaults.
                                vrcSo.Update();
                                vrcParametersProp.GetArrayElementAtIndex(vIndex)
                                    .FindPropertyRelative("defaultValue").floatValue = newVrcDefault;
                                vrcSo.ApplyModifiedPropertiesWithoutUndo();
                                EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                                AssetDatabase.SaveAssets();
                                _parameterListVrcVersion = -1;
                                Repaint();
                            }

                            EditorGUI.BeginChangeCheck();

                            int newTypeVal = HubIntPopup("VrcParameterType_" + _vrcExpressionParametersAsset.GetInstanceID() + "_" + vIndex,
                                typeProp.intValue, ParameterTypeLabels, VrcParameterTypeValues, GUILayout.Width(61));

                            GUILayout.Space(4);
                            if (EditorGUI.EndChangeCheck())
                            {
                                Undo.RegisterCompleteObjectUndo(_vrcExpressionParametersAsset, "Change VRC Parameter Type");
                                vrcSo.Update();
                                ConvertVrcParameterTypeAndDefault(vElem, newTypeVal);
                                vrcSo.ApplyModifiedPropertiesWithoutUndo();
                                EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                                AssetDatabase.SaveAssets();
                            }

                            SerializedProperty savedProp = vElem.FindPropertyRelative("saved");
                            GUILayout.Space(12);
                            EditorGUI.BeginChangeCheck();
                            bool newSaved = EditorGUILayout.Toggle(savedProp.boolValue, GUILayout.Width(33));
                            if (EditorGUI.EndChangeCheck())
                            {
                                Undo.RegisterCompleteObjectUndo(_vrcExpressionParametersAsset, "Change VRC Parameter Saved");
                                savedProp.boolValue = newSaved;
                                vrcSo.ApplyModifiedProperties();
                                EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                            }

                            SerializedProperty syncedProp = vElem.FindPropertyRelative("networkSynced");
                            GUILayout.Space(16);
                            if (syncedProp != null)
                            {
                                EditorGUI.BeginChangeCheck();
                                bool newSynced = EditorGUILayout.Toggle(syncedProp.boolValue, GUILayout.Width(34));
                                if (EditorGUI.EndChangeCheck())
                                {
                                    Undo.RegisterCompleteObjectUndo(_vrcExpressionParametersAsset, "Change VRC Parameter Synced");
                                    syncedProp.boolValue = newSynced;
                                    vrcSo.ApplyModifiedProperties();
                                    EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                                }
                            }
                            else
                            {
                                GUILayout.Space(34);
                            }

                            GUI.backgroundColor = new Color(0.4f, 0.85f, 0.5f);
                            if (GUILayout.Button(new GUIContent("+ Animator", "Add this VRC parameter to Animator at its displayed position."), EditorStyles.miniButton, GUILayout.Width(75)))
                            {
                                vrcParamToAdd = vIndex;
                            }
                            GUI.backgroundColor = originalBgColor;

                            GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                            if (GUILayout.Button("X", GUILayout.Width(22)))
                            {
                                vrcOnlyToDelete = vIndex;
                            }
                            GUI.backgroundColor = originalBgColor;

                            EditorGUILayout.EndHorizontal();
                        }
                        GUILayout.EndArea();
                    }
                }

                bool parameterFieldsChanged = EditorGUI.EndChangeCheck();
                if (parameterFieldsChanged || animatorParamToAddToVrc >= 0 || vrcParamToAdd >= 0 ||
                    vrcOnlyToDelete >= 0 || vrcParamToRemove >= 0 || paramToDelete >= 0)
                    _parameterListControllerVersion = _parameterListVrcVersion = -1;

                if (animatorParamToAddToVrc >= 0)
                    AddVrcParameter(controller, _vrcExpressionParametersAsset, parameters[animatorParamToAddToVrc].name);

                if (vrcParamToAdd != -1)
                {
                    SerializedProperty toAddElem = vrcParametersProp.GetArrayElementAtIndex(vrcParamToAdd);
                    string toAddName = toAddElem.FindPropertyRelative("name").stringValue;
                    int toAddType = toAddElem.FindPropertyRelative("valueType").intValue;
                    float toAddDef = toAddElem.FindPropertyRelative("defaultValue").floatValue;

                    AddVrcOnlyParamToController(controller, toAddName, toAddType, toAddDef);
                }

                if (vrcOnlyToDelete != -1)
                {
                    string toDeleteName = vrcParametersProp.GetArrayElementAtIndex(vrcOnlyToDelete).FindPropertyRelative("name").stringValue;
                    RemoveParameterFromVRC(toDeleteName);
                }

                if (toggleSelectIndex != -2)
                {
                    _selectedParamIndex = toggleSelectIndex;
                    if (_showParameterUsage && _parameterUsageFollowSelection) Repaint();
                }

                if (!needsResolution && !isSearching && _parameterRows.Count > 0 && _isParamDragging && _paramDragSourceIndex >= 0 && _paramDragSourceIndex < parameters.Length)
                {
                    if (e.type == EventType.MouseDrag)
                    {
                        int hoverRow = Mathf.Clamp(Mathf.FloorToInt(parameterPointer.y / parameterRowHeight), 0, _parameterRows.Count - 1);
                        int hoverIndex = _parameterRows[hoverRow].animatorIndex;
                        if (hoverIndex < 0)
                        {
                            // A VRC-only row represents a gap in the Animator list, not an Animator index.
                            int insertionIndex = 0;
                            for (int rowIndex = hoverRow - 1; rowIndex >= 0; rowIndex--)
                                if (_parameterRows[rowIndex].animatorIndex >= 0)
                                {
                                    insertionIndex = _parameterRows[rowIndex].animatorIndex + 1;
                                    break;
                                }
                            hoverIndex = insertionIndex > _paramDragSourceIndex ? insertionIndex - 1 : insertionIndex;
                        }
                        hoverIndex = Mathf.Clamp(hoverIndex, 0, parameters.Length - 1);

                        if (hoverIndex != _paramDragSourceIndex)
                        {
                            if (!_paramDragHasMoved) Undo.RegisterCompleteObjectUndo(controller, "Reorder Parameters");
                            var list = new List<AnimatorControllerParameter>(parameters);
                            var item = list[_paramDragSourceIndex];

                            if (_selectedParamIndex == _paramDragSourceIndex)
                            {
                                _selectedParamIndex = hoverIndex;
                            }
                            else if (_selectedParamIndex > _paramDragSourceIndex && _selectedParamIndex <= hoverIndex)
                            {
                                _selectedParamIndex--;
                            }
                            else if (_selectedParamIndex < _paramDragSourceIndex && _selectedParamIndex >= hoverIndex)
                            {
                                _selectedParamIndex++;
                            }

                            list.RemoveAt(_paramDragSourceIndex);
                            list.Insert(hoverIndex, item);

                            parameters = list.ToArray();
                            controller.parameters = parameters;
                            _parameterListParameters = parameters;
                            _parameterListControllerVersion = -1;
                            _paramDragSourceIndex = hoverIndex;
                            _paramDragHasMoved = true;
                            Repaint();
                        }
                        e.Use();
                    }
                    else if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp)
                    {
                        EditorUtility.SetDirty(controller);

                        if (_paramDragHasMoved && _vrcExpressionParametersAsset != null)
                        {
                            using var vrcSo2 = new SerializedObject(_vrcExpressionParametersAsset);
                            SerializedProperty pProp = vrcSo2.FindProperty("parameters");
                            if (pProp != null && pProp.isArray)
                            {
                                ReorderVRCParameters(controller, pProp);
                                vrcSo2.ApplyModifiedProperties();
                                EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                                AssetDatabase.SaveAssets();
                            }
                        }

                        ResetParameterDrag();
                        Repaint();
                        e.Use();
                    }
                }

                if (vrcParamToRemove != -1)
                {
                    RemoveParameterFromVRC(parameters[vrcParamToRemove].name);
                    vrcParamToRemove = -1;
                }

                if (paramToDelete != -1)
                {
                    string deletedParamName = parameters[paramToDelete].name;

                    bool proceedWithDeletion = true;
                    if (IsParameterUsedAnywhere(controller, deletedParamName))
                    {
                        proceedWithDeletion = EditorUtility.DisplayDialog(
                            "Parameter in Use",
                            $"The parameter '{deletedParamName}' is currently being used by transitions, states, or blend trees in this Animator Controller.\n\nAre you sure you want to delete it?",
                            "Yes, Delete",
                            "Cancel"
                        );
                    }

                    if (proceedWithDeletion)
                    {
                        if (_selectedParamIndex == paramToDelete)
                        {
                            _selectedParamIndex = -1;
                        }
                        else if (_selectedParamIndex > paramToDelete)
                        {
                            _selectedParamIndex--;
                        }

                        Undo.RecordObject(controller, "Delete Parameter");
                        controller.RemoveParameter(parameters[paramToDelete]);
                        EditorUtility.SetDirty(controller);

                        if (_vrcExpressionParametersAsset != null)
                        {
                            RemoveParameterFromVRC(deletedParamName);
                        }
                    }
                }

                paramScrollView.Dispose();
                if (e.type == EventType.Repaint) _parameterViewportRect = GUILayoutUtility.GetLastRect();
                EditorGUILayout.EndVertical();

                Rect resizerRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(6), GUILayout.ExpandWidth(true));
                EditorGUIUtility.AddCursorRect(resizerRect, MouseCursor.ResizeVertical);

                if (e.type == EventType.Repaint)
                {
                    Rect lineRect = new Rect(resizerRect.x, resizerRect.y + 2, resizerRect.width, 2);
                    EditorGUI.DrawRect(lineRect, new Color(0.15f, 0.15f, 0.15f, 1f));
                }

                if (e.type == EventType.MouseDown && e.button == 0 && resizerRect.Contains(e.mousePosition))
                {
                    _isResizingParamList = true;
                    e.Use();
                }
                if (_isResizingParamList && e.type == EventType.MouseDrag)
                {
                    _paramListHeight += e.delta.y;
                    _paramListHeight = Mathf.Clamp(_paramListHeight, 60f, 1000f);
                    Repaint();
                    e.Use();
                }
                if (_isResizingParamList && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp))
                {
                    _isResizingParamList = false;
                    EditorPrefs.SetFloat(PrefParamListHeightKey, _paramListHeight);
                    e.Use();
                }

                if (hasTypeChange)
                {
                    EditorApplication.delayCall += () =>
                    {
                        if (controller != null)
                        {
                            Undo.RecordObject(controller, "Change Parameter Type");
                            ConvertParameterType(controller, typeChangeParamName, oldParamType, newParamType);
                            _parameterListControllerVersion = _parameterListVrcVersion = -1;
                        }
                    };
                }
            }
        }

        private HashSet<string> GetParametersUsedInBlendTrees(AnimatorController controller)
        {
            HashSet<string> usedParams = new HashSet<string>();
            _blendParameterVersions.Clear();
            if (controller == null) return usedParams;
            _blendParameterVersions[controller] = EditorUtility.GetDirtyCount(controller);
            HashSet<BlendTree> visitedTrees = new HashSet<BlendTree>();
            var visitedMachines = new HashSet<AnimatorStateMachine>();

            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine != null)
                {
                    CollectParametersFromStateMachine(layer.stateMachine, usedParams, visitedTrees, visitedMachines);
                }
            }
            return usedParams;
        }

        private void CollectParametersFromStateMachine(AnimatorStateMachine sm, HashSet<string> usedParams, HashSet<BlendTree> visitedTrees, HashSet<AnimatorStateMachine> visitedMachines)
        {
            if (sm == null || !visitedMachines.Add(sm)) return;
            _blendParameterVersions[sm] = EditorUtility.GetDirtyCount(sm);
            foreach (var state in sm.states)
            {
                if (state.state != null)
                {
                    _blendParameterVersions[state.state] = EditorUtility.GetDirtyCount(state.state);
                    if (state.state.timeParameterActive && !string.IsNullOrEmpty(state.state.timeParameter)) usedParams.Add(state.state.timeParameter);
                    if (state.state.speedParameterActive && !string.IsNullOrEmpty(state.state.speedParameter)) usedParams.Add(state.state.speedParameter);

                    if (state.state.motion is BlendTree bt)
                    {
                        CollectParametersFromBlendTree(bt, usedParams, visitedTrees);
                    }
                }
            }
            foreach (var childSm in sm.stateMachines)
            {
                CollectParametersFromStateMachine(childSm.stateMachine, usedParams, visitedTrees, visitedMachines);
            }
        }

        private void CollectParametersFromBlendTree(BlendTree tree, HashSet<string> usedParams, HashSet<BlendTree> visitedTrees)
        {
            if (tree == null || !visitedTrees.Add(tree)) return;
            _blendParameterVersions[tree] = EditorUtility.GetDirtyCount(tree);
            if (!string.IsNullOrEmpty(tree.blendParameter)) usedParams.Add(tree.blendParameter);
            if (!string.IsNullOrEmpty(tree.blendParameterY)) usedParams.Add(tree.blendParameterY);

            foreach (var child in tree.children)
            {
                if (!string.IsNullOrEmpty(child.directBlendParameter)) usedParams.Add(child.directBlendParameter);
                if (child.motion is BlendTree subTree)
                {
                    CollectParametersFromBlendTree(subTree, usedParams, visitedTrees);
                }
            }
        }

        private void AddParameterToController(AnimatorController controller, string paramName, AnimatorControllerParameterType paramType)
        {
            _parameterListControllerVersion = -1;
            Undo.RecordObject(controller, "Add Parameter");

            bool forceBlendTreeWeight = _hideVrcBuiltInParameters && paramName == "BlendTreeWeight";
            if (forceBlendTreeWeight)
            {
                paramType = AnimatorControllerParameterType.Float;
            }

            string finalName = paramName;
            int counter = 1;
            while (true)
            {
                bool exists = false;
                foreach (var p in controller.parameters)
                {
                    if (p.name == finalName) { exists = true; break; }
                }
                if (!exists) break;
                finalName = paramName + "_" + counter;
                counter++;
            }

            controller.AddParameter(finalName, paramType);
            var list = new List<AnimatorControllerParameter>(controller.parameters);
            var newItem = list[list.Count - 1];
            list.RemoveAt(list.Count - 1);

            if (forceBlendTreeWeight && finalName == "BlendTreeWeight")
            {
                newItem.defaultFloat = 1f;
            }

            int insertIndex = list.Count;
            if (_paramAddPosition == AddPosition.AboveSelected)
            {
                insertIndex = _selectedParamIndex >= 0 ? _selectedParamIndex : 0;
            }
            else if (_paramAddPosition == AddPosition.BelowSelected)
            {
                insertIndex = _selectedParamIndex >= 0 ? Mathf.Min(list.Count, _selectedParamIndex + 1) : list.Count;
            }

            if (_selectedParamIndex >= insertIndex)
            {
                _selectedParamIndex++;
            }

            list.Insert(insertIndex, newItem);
            controller.parameters = list.ToArray();
            _selectedParamIndex = insertIndex;

            EditorUtility.SetDirty(controller);
        }

        private bool IsParameterUsedAnywhere(AnimatorController controller, string paramName)
        {
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine != null)
                {
                    if (IsParameterUsedInStateMachineOverall(layer.stateMachine, paramName)) return true;
                }
            }
            return false;
        }

        private bool IsParameterUsedInStateMachineOverall(AnimatorStateMachine sm, string paramName)
        {
            if (sm == null) return false;

            foreach (var trans in sm.anyStateTransitions)
            {
                if (trans != null)
                {
                    foreach (var cond in trans.conditions)
                        if (cond.parameter == paramName) return true;
                }
            }

            foreach (var state in sm.states)
            {
                if (state.state != null)
                {
                    if (state.state.timeParameterActive && state.state.timeParameter == paramName) return true;
                    if (state.state.speedParameterActive && state.state.speedParameter == paramName) return true;
                    if (state.state.cycleOffsetParameterActive && state.state.cycleOffsetParameter == paramName) return true;
                    if (state.state.mirrorParameterActive && state.state.mirrorParameter == paramName) return true;

                    foreach (var trans in state.state.transitions)
                    {
                        if (trans != null)
                        {
                            foreach (var cond in trans.conditions)
                                if (cond.parameter == paramName) return true;
                        }
                    }

                    if (state.state.motion is BlendTree bt)
                    {
                        if (IsParameterInBlendTreeRecursive(bt, paramName)) return true;
                    }
                }
            }

            foreach (var childSm in sm.stateMachines)
            {
                if (IsParameterUsedInStateMachineOverall(childSm.stateMachine, paramName)) return true;
            }

            return false;
        }

        private bool IsParameterUsedInBlendTrees(AnimatorController controller, string paramName)
        {
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine != null)
                {
                    if (IsParameterInStateMachineBlendTrees(layer.stateMachine, paramName)) return true;
                }
            }
            return false;
        }

        private bool IsParameterInStateMachineBlendTrees(AnimatorStateMachine sm, string paramName)
        {
            foreach (var state in sm.states)
            {
                if (state.state != null)
                {
                    if (state.state.timeParameterActive && state.state.timeParameter == paramName) return true;
                    if (state.state.speedParameterActive && state.state.speedParameter == paramName) return true;

                    if (state.state.motion is BlendTree bt)
                    {
                        if (IsParameterInBlendTreeRecursive(bt, paramName)) return true;
                    }
                }
            }
            foreach (var childSm in sm.stateMachines)
            {
                if (IsParameterInStateMachineBlendTrees(childSm.stateMachine, paramName)) return true;
            }
            return false;
        }

        private bool IsParameterInBlendTreeRecursive(BlendTree tree, string paramName)
        {
            return IsParameterInBlendTreeRecursive(tree, paramName, new HashSet<BlendTree>());
        }

        private bool IsParameterInBlendTreeRecursive(BlendTree tree, string paramName, HashSet<BlendTree> visitedTrees)
        {
            if (tree == null || !visitedTrees.Add(tree)) return false;
            if (tree.blendParameter == paramName || tree.blendParameterY == paramName) return true;

            foreach (var child in tree.children)
            {
                if (child.directBlendParameter == paramName) return true;
                if (child.motion is BlendTree subTree)
                {
                    if (IsParameterInBlendTreeRecursive(subTree, paramName, visitedTrees)) return true;
                }
            }
            return false;
        }

        private void RenameParameterEverywhere(AnimatorController controller, string oldName, string newName)
        {
            HashSet<BlendTree> visitedTrees = new HashSet<BlendTree>();
            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine != null)
                {
                    RenameParameterInStateMachine(layer.stateMachine, oldName, newName, visitedTrees);
                }
            }

            bool isInVrcList = false;

            if (_vrcExpressionParametersAsset != null)
            {
                SerializedObject so = new SerializedObject(_vrcExpressionParametersAsset);
                SerializedProperty parametersProp = so.FindProperty("parameters");
                if (parametersProp != null && parametersProp.isArray)
                {
                    Undo.RegisterCompleteObjectUndo(_vrcExpressionParametersAsset, "Rename Parameter in VRC Expression Parameters");
                    for (int i = 0; i < parametersProp.arraySize; i++)
                    {
                        SerializedProperty element = parametersProp.GetArrayElementAtIndex(i);
                        SerializedProperty nameProp = element.FindPropertyRelative("name");
                        if (nameProp.stringValue == oldName)
                        {
                            nameProp.stringValue = newName;
                            isInVrcList = true;
                        }
                    }
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(_vrcExpressionParametersAsset);
                    AssetDatabase.SaveAssets();
                }
            }

            if (_vrcExpressionMenuAsset != null && isInVrcList)
            {
                RenameParameterInMenuRecursive(_vrcExpressionMenuAsset, oldName, newName, new HashSet<UnityEngine.Object>());
            }
        }

        private void RenameParameterInMenuRecursive(UnityEngine.Object menuObj, string oldName, string newName, HashSet<UnityEngine.Object> visitedMenus)
        {
            if (menuObj == null || !visitedMenus.Add(menuObj)) return;

            SerializedObject so = new SerializedObject(menuObj);
            so.Update();

            SerializedProperty controlsProp = so.FindProperty("controls");
            if (controlsProp == null || !controlsProp.isArray) return;

            Undo.RegisterCompleteObjectUndo(menuObj, "Rename Parameter in VRC Expression Menu");
            bool modified = false;

            for (int i = 0; i < controlsProp.arraySize; i++)
            {
                SerializedProperty control = controlsProp.GetArrayElementAtIndex(i);

                SerializedProperty paramProp = control.FindPropertyRelative("parameter.name");
                if (paramProp != null && paramProp.stringValue == oldName)
                {
                    paramProp.stringValue = newName;
                    modified = true;
                }

                SerializedProperty subParamsProp = control.FindPropertyRelative("subParameters");
                if (subParamsProp != null && subParamsProp.isArray)
                {
                    for (int j = 0; j < subParamsProp.arraySize; j++)
                    {
                        SerializedProperty subParamProp = subParamsProp.GetArrayElementAtIndex(j).FindPropertyRelative("name");
                        if (subParamProp != null && subParamProp.stringValue == oldName)
                        {
                            subParamProp.stringValue = newName;
                            modified = true;
                        }
                    }
                }

                SerializedProperty subMenuProp = control.FindPropertyRelative("subMenu");
                if (subMenuProp != null && subMenuProp.objectReferenceValue != null)
                {
                    RenameParameterInMenuRecursive(subMenuProp.objectReferenceValue, oldName, newName, visitedMenus);
                }
            }

            if (modified)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(menuObj);
                AssetDatabase.SaveAssets();
            }
        }

        private void RenameParameterInStateMachine(AnimatorStateMachine sm, string oldName, string newName, HashSet<BlendTree> visitedTrees)
        {
            if (sm == null) return;
            foreach (var state in sm.states)
            {
                if (state.state == null) continue;

                Undo.RecordObject(state.state, "Update State Speed/Time Parameter Name");
                if (state.state.speedParameter == oldName) state.state.speedParameter = newName;
                if (state.state.timeParameter == oldName) state.state.timeParameter = newName;

                foreach (var trans in state.state.transitions)
                {
                    if (trans == null) continue;
                    Undo.RecordObject(trans, "Update Transition Parameter Name");
                    AnimatorCondition[] conditions = trans.conditions;
                    bool modified = false;
                    for (int c = 0; c < conditions.Length; c++)
                    {
                        if (conditions[c].parameter == oldName)
                        {
                            conditions[c].parameter = newName;
                            modified = true;
                        }
                    }
                    if (modified)
                    {
                        trans.conditions = conditions;
                        EditorUtility.SetDirty(trans);
                    }
                }

                var behaviours = state.state.behaviours;
                foreach (var behaviour in behaviours)
                {
                    if (behaviour != null)
                    {
                        SerializedObject so = new SerializedObject(behaviour);
                        SerializedProperty prop = so.GetIterator();
                        bool behModified = false;
                        while (prop.NextVisible(true))
                        {
                            if (prop.propertyType == SerializedPropertyType.String && prop.stringValue == oldName)
                            {
                                prop.stringValue = newName;
                                behModified = true;
                            }
                        }
                        if (behModified)
                        {
                            so.ApplyModifiedProperties();
                            EditorUtility.SetDirty(behaviour);
                        }
                    }
                }

                if (state.state.motion is BlendTree bt)
                {
                    RenameParameterInBlendTreeRecursive(bt, oldName, newName, visitedTrees);
                }
            }

            foreach (var trans in sm.anyStateTransitions)
            {
                if (trans == null) continue;
                Undo.RecordObject(trans, "Update AnyState Transition Parameter Name");
                AnimatorCondition[] conditions = trans.conditions;
                bool modified = false;
                for (int c = 0; c < conditions.Length; c++)
                {
                    if (conditions[c].parameter == oldName)
                    {
                        conditions[c].parameter = newName;
                        modified = true;
                    }
                }
                if (modified)
                {
                    trans.conditions = conditions;
                    EditorUtility.SetDirty(trans);
                }
            }

            foreach (var childSm in sm.stateMachines)
            {
                RenameParameterInStateMachine(childSm.stateMachine, oldName, newName, visitedTrees);
            }
        }

        private void RenameParameterInBlendTreeRecursive(BlendTree tree, string oldName, string newName, HashSet<BlendTree> visitedTrees)
        {
            if (tree == null || !visitedTrees.Add(tree)) return;
            Undo.RecordObject(tree, "Update Blend Tree Parameter Name");

            bool modified = false;
            if (tree.blendParameter == oldName)
            {
                tree.blendParameter = newName;
                modified = true;
            }
            if (tree.blendParameterY == oldName)
            {
                tree.blendParameterY = newName;
                modified = true;
            }

            ChildMotion[] children = tree.children;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].directBlendParameter == oldName)
                {
                    children[i].directBlendParameter = newName;
                    modified = true;
                }
                if (children[i].motion is BlendTree subTree)
                {
                    RenameParameterInBlendTreeRecursive(subTree, oldName, newName, visitedTrees);
                }
            }

            if (modified)
            {
                tree.children = children;
                EditorUtility.SetDirty(tree);
            }
        }

        private void ConvertParameterType(AnimatorController controller, string paramName, AnimatorControllerParameterType oldType,
            AnimatorControllerParameterType newType, bool updateVrcParameters = true, bool saveAssets = true, bool recordTransitionUndo = true)
        {
            // Only rewrite Animator conditions when its parameter type actually changes.
            if (oldType != newType)
            {
                AnimatorControllerParameter[] paramsList = controller.parameters;
                for (int i = 0; i < paramsList.Length; i++)
                {
                    if (paramsList[i].name == paramName)
                    {
                        // Read the active default before changing the type; inactive default
                        // fields may still contain values left over from an earlier type.
                        double value = ConvertParameterDefault(GetAnimatorParameterDefault(paramsList[i]), oldType, newType);
                        if (newType == AnimatorControllerParameterType.Float) paramsList[i].defaultFloat = (float)value;
                        else if (newType == AnimatorControllerParameterType.Int) paramsList[i].defaultInt = (int)value;
                        else if (newType == AnimatorControllerParameterType.Bool) paramsList[i].defaultBool = value > 0d;
                        paramsList[i].type = newType;
                        break;
                    }
                }
                controller.parameters = paramsList;
                EditorUtility.SetDirty(controller);

                foreach (var layer in controller.layers)
                    if (layer.stateMachine != null)
                        UpdateTransitionsInStateMachine(layer.stateMachine, paramName, oldType, newType, null, recordTransitionUndo);
            }

            var vrcAsset = _vrcExpressionParametersAsset;
            if (updateVrcParameters && vrcAsset != null)
            {
                using var so = new SerializedObject(vrcAsset);
                SerializedProperty parametersProp = so.FindProperty("parameters");
                if (parametersProp != null && parametersProp.isArray)
                {
                    for (int i = 0; i < parametersProp.arraySize; i++)
                    {
                        SerializedProperty element = parametersProp.GetArrayElementAtIndex(i);
                        if (element.FindPropertyRelative("name").stringValue == paramName)
                        {
                            int typeVal = 1;
                            if (newType == AnimatorControllerParameterType.Int) typeVal = 0;
                            else if (newType == AnimatorControllerParameterType.Bool) typeVal = 2;

                            ConvertVrcParameterTypeAndDefault(element, typeVal);
                            break;
                        }
                    }
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(vrcAsset);
                }
            }
            if (saveAssets) AssetDatabase.SaveAssets();
        }

        private void UpdateTransitionsInStateMachine(AnimatorStateMachine sm, string paramName, AnimatorControllerParameterType oldType,
            AnimatorControllerParameterType newType, HashSet<AnimatorStateMachine> visited = null, bool recordUndo = true)
        {
            if (sm == null) return;
            if (visited == null) visited = new HashSet<AnimatorStateMachine>();
            if (!visited.Add(sm)) return;
            foreach (var state in sm.states)
            {
                if (state.state == null) continue;
                foreach (var trans in state.state.transitions)
                {
                    UpdateTransitionConditions(trans, paramName, oldType, newType, recordUndo);
                }
            }

            foreach (var trans in sm.anyStateTransitions)
            {
                UpdateTransitionConditions(trans, paramName, oldType, newType, recordUndo);
            }

            foreach (var trans in sm.entryTransitions)
                UpdateTransitionConditions(trans, paramName, oldType, newType, recordUndo);

            foreach (var childSm in sm.stateMachines)
            {
                if (childSm.stateMachine == null) continue;
                foreach (var trans in sm.GetStateMachineTransitions(childSm.stateMachine))
                    UpdateTransitionConditions(trans, paramName, oldType, newType, recordUndo);
                UpdateTransitionsInStateMachine(childSm.stateMachine, paramName, oldType, newType, visited, recordUndo);
            }
        }

        private void UpdateTransitionConditions(AnimatorTransitionBase trans, string paramName, AnimatorControllerParameterType oldType,
            AnimatorControllerParameterType newType, bool recordUndo = true)
        {
            if (trans == null) return;
            AnimatorCondition[] conditions = trans.conditions;
            bool modified = false;

            for (int i = 0; i < conditions.Length; i++)
            {
                if (conditions[i].parameter == paramName)
                {
                    if ((oldType == AnimatorControllerParameterType.Bool || oldType == AnimatorControllerParameterType.Trigger) &&
                        newType == AnimatorControllerParameterType.Float)
                    {
                        if (conditions[i].mode == AnimatorConditionMode.If)
                        {
                            conditions[i].mode = AnimatorConditionMode.Greater;
                            conditions[i].threshold = 0.5f;
                            modified = true;
                        }
                        else if (conditions[i].mode == AnimatorConditionMode.IfNot)
                        {
                            conditions[i].mode = AnimatorConditionMode.Less;
                            conditions[i].threshold = 0.5f;
                            modified = true;
                        }
                    }
                    else if ((oldType == AnimatorControllerParameterType.Bool || oldType == AnimatorControllerParameterType.Trigger) &&
                             newType == AnimatorControllerParameterType.Int)
                    {
                        if (conditions[i].mode == AnimatorConditionMode.If)
                        {
                            conditions[i].mode = AnimatorConditionMode.Equals;
                            conditions[i].threshold = 1f;
                            modified = true;
                        }
                        else if (conditions[i].mode == AnimatorConditionMode.IfNot)
                        {
                            conditions[i].mode = AnimatorConditionMode.Equals;
                            conditions[i].threshold = 0f;
                            modified = true;
                        }
                    }
                    else if ((oldType == AnimatorControllerParameterType.Float || oldType == AnimatorControllerParameterType.Int) && newType == AnimatorControllerParameterType.Bool)
                    {
                        if (conditions[i].mode == AnimatorConditionMode.Greater && conditions[i].threshold >= 0.5f)
                        {
                            conditions[i].mode = AnimatorConditionMode.If;
                            modified = true;
                        }
                        else if (conditions[i].mode == AnimatorConditionMode.Less && conditions[i].threshold <= 0.5f)
                        {
                            conditions[i].mode = AnimatorConditionMode.IfNot;
                            modified = true;
                        }
                        else if (conditions[i].mode == AnimatorConditionMode.Equals && Mathf.Approximately(conditions[i].threshold, 1f))
                        {
                            conditions[i].mode = AnimatorConditionMode.If;
                            modified = true;
                        }
                        else if (conditions[i].mode == AnimatorConditionMode.Equals && Mathf.Approximately(conditions[i].threshold, 0f))
                        {
                            conditions[i].mode = AnimatorConditionMode.IfNot;
                            modified = true;
                        }
                    }
                    else if ((oldType == AnimatorControllerParameterType.Int || oldType == AnimatorControllerParameterType.Float) &&
                             (newType == AnimatorControllerParameterType.Int || newType == AnimatorControllerParameterType.Float))
                    {
                        if (conditions[i].mode == AnimatorConditionMode.Equals && Mathf.Approximately(conditions[i].threshold, 1f))
                        {
                            conditions[i].mode = AnimatorConditionMode.Greater;
                            conditions[i].threshold = 0.5f;
                            modified = true;
                        }
                        else if (conditions[i].mode == AnimatorConditionMode.Equals && Mathf.Approximately(conditions[i].threshold, 0f))
                        {
                            conditions[i].mode = AnimatorConditionMode.Less;
                            conditions[i].threshold = 0.5f;
                            modified = true;
                        }
                        else if (conditions[i].mode == AnimatorConditionMode.Greater && conditions[i].threshold >= 0.5f)
                        {
                            conditions[i].mode = AnimatorConditionMode.Equals;
                            conditions[i].threshold = 1f;
                            modified = true;
                        }
                        else if (conditions[i].mode == AnimatorConditionMode.Less && conditions[i].threshold <= 0.5f)
                        {
                            conditions[i].mode = AnimatorConditionMode.Equals;
                            conditions[i].threshold = 0f;
                            modified = true;
                        }
                    }
                }
            }

            if (modified)
            {
                if (recordUndo) Undo.RecordObject(trans, "Update Transition Conditions");
                trans.conditions = conditions;
                EditorUtility.SetDirty(trans);
            }
        }

        private void DrawRootSelection()
        {
            GUILayout.Space(5);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _searchQuery = EditorGUILayout.TextField(
                new GUIContent("Find blend tree", "Search root Blend Tree names."),
                _searchQuery, EditorStyles.toolbarSearchField);
            if (EditorGUI.EndChangeCheck()) _rootScrollPos = Vector2.zero;
            if (GUILayout.Button("Clear", GUILayout.Width(45)))
            {
                _searchQuery = "";
                _rootScrollPos = Vector2.zero;
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Root Blend Trees", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("+ New Blend Tree", GUILayout.Width(160)))
            {
                CreateNewRootBlendTreeWithLayer();
            }
            if (GUILayout.Button("Refresh", GUILayout.Width(70)))
            {
                RefreshAvailableTrees();
            }
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(5);

            using var rootScrollView = new NestedScrollViewScope(ref _rootScrollPos, GUILayout.Height(_blendTreeListHeight));

            BlendTree rootToDelete = null;

            foreach (var root in _rootTrees)
            {
                if (root == null) continue;
                if (!string.IsNullOrEmpty(_searchQuery) && root.name.IndexOf(_searchQuery, System.StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                EditorGUILayout.BeginHorizontal("box");
                GUILayout.Label($"[{root.blendType}]", GUILayout.Width(130));
                GUILayout.Label(root.name, GUILayout.ExpandWidth(true));
                GUILayout.Label($"{root.children.Length} motions", GUILayout.Width(80));

                if (GUILayout.Button("Open", GUILayout.Width(60)))
                {
                    GUI.FocusControl(null);
                    _navigationPath.Add(root);
                    GUIUtility.ExitGUI();
                }

                GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                if (GUILayout.Button("X", GUILayout.Width(25)))
                {
                    rootToDelete = root;
                }
                GUI.backgroundColor = Color.white;

                EditorGUILayout.EndHorizontal();
            }

            if (rootToDelete != null)
            {
                DeleteRootTree(rootToDelete);
            }

            rootScrollView.Dispose();

            DrawBlendTreeListResizer();
        }

        private void CreateNewRootBlendTreeWithLayer()
        {
            if (_targetAsset is AnimatorController controller)
            {
                Undo.RecordObject(controller, "Create Root Blend Tree Layer and State");

                string layerName = GetUniqueLayerName(controller, "BlendTree Layer");

                controller.AddLayer(layerName);
                AnimatorControllerLayer[] layers = controller.layers;
                int newLayerIndex = layers.Length - 1;
                layers[newLayerIndex].defaultWeight = 1.0f;
                controller.layers = layers;

                controller.CreateBlendTreeInController(
                    "Root Blend Tree",
                    out BlendTree newRoot,
                    newLayerIndex);

                newRoot.blendType = BlendTreeType.Direct;
                newRoot.hideFlags |= HideFlags.HideInHierarchy;
                EditorUtility.SetDirty(newRoot);

                GetOrCreateFirstFloatParameter();

                AssetDatabase.SaveAssets();
                RefreshAvailableTrees();
            }
            else
            {
                EditorUtility.DisplayDialog("Error", "Please drag an FX Animator Controller into the slot to create a Blend Tree.", "OK");
            }

        }

        private string GetUniqueLayerName(AnimatorController controller, string baseName)
        {
            string name = baseName;
            int counter = 1;
            bool exists;
            do
            {
                exists = false;
                foreach (var layer in controller.layers)
                {
                    if (layer.name == name)
                    {
                        exists = true;
                        name = baseName + "_" + counter;
                        counter++;
                        break;
                    }
                }
            } while (exists);
            return name;
        }

        private void DeleteRootTree(BlendTree root)
        {
            if (EditorUtility.DisplayDialog("Delete Root Tree", $"Are you sure you want to delete '{root.name}'? This will also attempt to clean up its layer and state in the Animator.", "Yes", "No"))
            {
                HashSet<AnimationClip> generatedClips = new HashSet<AnimationClip>();
                CollectGeneratedConverterClips(root, generatedClips, new HashSet<BlendTree>());

                if (_targetAsset is AnimatorController controller)
                {
                    Undo.RecordObject(controller, "Delete Root Blend Tree Layer");

                    AnimatorControllerLayer[] layers = controller.layers;
                    int layerToRemove = -1;

                    for (int i = 0; i < layers.Length; i++)
                    {
                        if (layers[i].stateMachine != null)
                        {
                            foreach (var state in layers[i].stateMachine.states)
                            {
                                if (state.state.motion == root)
                                {
                                    layerToRemove = i;
                                    break;
                                }
                            }
                        }
                        if (layerToRemove != -1) break;
                    }

                    if (layerToRemove != -1)
                    {
                        controller.RemoveLayer(layerToRemove);
                    }
                }

                DestroyBlendTreeRecursive(root);
                DeleteGeneratedConverterClipAssets(generatedClips);
                AssetDatabase.SaveAssets();
                RefreshAvailableTrees();
            }
        }

        private bool IsGeneratedConverterBlendTree(BlendTree tree)
        {
            if (tree == null || tree.blendType != BlendTreeType.Simple1D)
                return false;

            ChildMotion[] children = tree.children;
            if (children == null || children.Length == 0)
                return false;

            bool hasGeneratedClip = false;
            bool allChildrenGenerated = true;
            foreach (ChildMotion child in children)
            {
                if (child.motion is AnimationClip clip && IsGeneratedConverterClipAsset(clip))
                {
                    hasGeneratedClip = true;
                }
                else
                {
                    allChildrenGenerated = false;
                }
            }

            return hasGeneratedClip &&
                   (allChildrenGenerated || tree.name.EndsWith(" Generated", StringComparison.OrdinalIgnoreCase));
        }

        private bool IsGeneratedConverterClipAsset(AnimationClip clip)
        {
            if (clip == null)
                return false;

            string assetPath = AssetDatabase.GetAssetPath(clip).Replace('\\', '/');
            string directoryName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(assetPath));
            string fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);

            return assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(directoryName, "Generated", StringComparison.OrdinalIgnoreCase) &&
                   fileName.IndexOf(" KeyFrame-", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void CollectGeneratedConverterClips(
            BlendTree tree,
            HashSet<AnimationClip> generatedClips,
            HashSet<BlendTree> visitedTrees)
        {
            if (tree == null || !visitedTrees.Add(tree))
                return;

            if (IsGeneratedConverterBlendTree(tree))
            {
                foreach (ChildMotion child in tree.children)
                {
                    if (child.motion is AnimationClip clip && IsGeneratedConverterClipAsset(clip))
                        generatedClips.Add(clip);
                }
            }

            foreach (ChildMotion child in tree.children)
            {
                if (child.motion is BlendTree childTree)
                    CollectGeneratedConverterClips(childTree, generatedClips, visitedTrees);
            }
        }

        private void DeleteGeneratedConverterClipAssets(HashSet<AnimationClip> generatedClips)
        {
            if (generatedClips == null || generatedClips.Count == 0)
                return;

            AssetDatabase.SaveAssets();
            HashSet<AnimationClip> clipsStillUsed = FindGeneratedClipsStillReferenced(generatedClips);
            bool deletedAsset = false;
            foreach (AnimationClip clip in generatedClips)
            {
                if (clip == null || clipsStillUsed.Contains(clip))
                    continue;

                string assetPath = AssetDatabase.GetAssetPath(clip);
                if (string.IsNullOrEmpty(assetPath) ||
                    !IsGeneratedConverterClipAsset(clip) ||
                    AssetDatabase.LoadMainAssetAtPath(assetPath) != clip)
                {
                    continue;
                }

                deletedAsset |= AssetDatabase.DeleteAsset(assetPath);
            }

            if (deletedAsset)
            {
                AssetDatabase.SaveAssets();
                EditorApplication.RepaintProjectWindow();
            }
        }

        private HashSet<AnimationClip> FindGeneratedClipsStillReferenced(HashSet<AnimationClip> candidateClips)
        {
            HashSet<AnimationClip> referencedClips = new HashSet<AnimationClip>();
            HashSet<BlendTree> visitedTrees = new HashSet<BlendTree>();
            HashSet<string> scannedControllerPaths = new HashSet<string>();

            string[] controllerGuids = AssetDatabase.FindAssets("t:AnimatorController");
            foreach (string guid in controllerGuids)
            {
                string controllerPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(controllerPath) || !scannedControllerPaths.Add(controllerPath))
                    continue;

                AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
                if (controller == null)
                    continue;

                foreach (AnimatorControllerLayer layer in controller.layers)
                {
                    CollectGeneratedClipReferencesInStateMachine(
                        layer.stateMachine,
                        candidateClips,
                        referencedClips,
                        visitedTrees);
                }
            }

            string[] blendTreeGuids = AssetDatabase.FindAssets("t:BlendTree");
            HashSet<string> scannedBlendTreePaths = new HashSet<string>();
            foreach (string guid in blendTreeGuids)
            {
                string blendTreePath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(blendTreePath) || !scannedBlendTreePaths.Add(blendTreePath))
                    continue;

                foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(blendTreePath))
                {
                    if (asset is BlendTree blendTree)
                    {
                        CollectGeneratedClipReferencesInBlendTree(
                            blendTree,
                            candidateClips,
                            referencedClips,
                            visitedTrees);
                    }
                }
            }

            return referencedClips;
        }

        private void CollectGeneratedClipReferencesInStateMachine(
            AnimatorStateMachine stateMachine,
            HashSet<AnimationClip> candidateClips,
            HashSet<AnimationClip> referencedClips,
            HashSet<BlendTree> visitedTrees)
        {
            if (stateMachine == null)
                return;

            foreach (ChildAnimatorState childState in stateMachine.states)
            {
                Motion motion = childState.state != null ? childState.state.motion : null;
                if (motion is AnimationClip clip && candidateClips.Contains(clip))
                    referencedClips.Add(clip);
                else if (motion is BlendTree blendTree)
                    CollectGeneratedClipReferencesInBlendTree(blendTree, candidateClips, referencedClips, visitedTrees);
            }

            foreach (ChildAnimatorStateMachine childStateMachine in stateMachine.stateMachines)
            {
                CollectGeneratedClipReferencesInStateMachine(
                    childStateMachine.stateMachine,
                    candidateClips,
                    referencedClips,
                    visitedTrees);
            }
        }

        private void CollectGeneratedClipReferencesInBlendTree(
            BlendTree tree,
            HashSet<AnimationClip> candidateClips,
            HashSet<AnimationClip> referencedClips,
            HashSet<BlendTree> visitedTrees)
        {
            if (tree == null || !visitedTrees.Add(tree))
                return;

            foreach (ChildMotion child in tree.children)
            {
                if (child.motion is AnimationClip clip && candidateClips.Contains(clip))
                    referencedClips.Add(clip);
                else if (child.motion is BlendTree childTree)
                    CollectGeneratedClipReferencesInBlendTree(childTree, candidateClips, referencedClips, visitedTrees);
            }
        }

        private void DestroyBlendTreeRecursive(BlendTree tree)
        {
            DestroyBlendTreeRecursive(tree, new HashSet<BlendTree>());
        }

        private void DestroyBlendTreeRecursive(BlendTree tree, HashSet<BlendTree> visitedTrees)
        {
            if (tree == null || !visitedTrees.Add(tree)) return;
            foreach (var child in tree.children)
            {
                if (child.motion is BlendTree childTree)
                {
                    DestroyBlendTreeRecursive(childTree, visitedTrees);
                }
            }
            Undo.DestroyObjectImmediate(tree);
        }

        private void DrawBreadcrumbs()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (_targetAsset is RuntimeAnimatorController)
            {
                if (GUILayout.Button("Roots", EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                {
                    GUI.FocusControl(null);
                    _navigationPath.Clear();
                    GUIUtility.ExitGUI();
                }
            }

            for (int i = 0; i < _navigationPath.Count; i++)
            {
                if (i > 0 || _targetAsset is RuntimeAnimatorController)
                {
                    GUILayout.Label(">", EditorStyles.label, GUILayout.Width(15));
                }

                if (_navigationPath[i] != null && GUILayout.Button(_navigationPath[i].name, EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                {
                    GUI.FocusControl(null);
                    int elementsToRemove = _navigationPath.Count - 1 - i;
                    if (elementsToRemove > 0)
                    {
                        _navigationPath.RemoveRange(i + 1, elementsToRemove);
                        GUIUtility.ExitGUI();
                    }
                }
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawBlendTreeSettings()
        {
            BlendTree tree = CurrentTree;
            if (tree == null) return;

            GUILayout.Space(10);
            EditorGUI.BeginChangeCheck();

            GUI.SetNextControlName("BTName_" + tree.GetInstanceID());
            string newName = EditorGUILayout.TextField("Blend Tree Name", tree.name);
            if (newName != tree.name)
            {
                Undo.RecordObject(tree, "Rename Blend Tree");
                tree.name = newName;
            }

            BlendTreeType newType = (BlendTreeType)HubEnumPopup("BlendTreeType_" + tree.GetInstanceID(), "Blend Type", tree.blendType);
            if (newType != tree.blendType)
            {
                Undo.RecordObject(tree, "Change Blend Type");
                tree.blendType = newType;
            }

            if (tree.blendType != BlendTreeType.Direct)
            {
                string paramIdX = "BTPX_" + tree.GetInstanceID();
                string paramX = DrawParameterComboBoxWithLabel(paramIdX, "Parameter", tree.blendParameter);
                if (paramX != tree.blendParameter)
                {
                    Undo.RecordObject(tree, "Change Parameter X");
                    tree.blendParameter = paramX;
                }

                if (Is2D(tree.blendType))
                {
                    string paramIdY = "BTPY_" + tree.GetInstanceID();
                    string paramY = DrawParameterComboBoxWithLabel(paramIdY, "Parameter Y", tree.blendParameterY);
                    if (paramY != tree.blendParameterY)
                    {
                        Undo.RecordObject(tree, "Change Parameter Y");
                        tree.blendParameterY = paramY;
                    }
                }
                else
                {
                    bool autoThresholds = EditorGUILayout.Toggle("Automate Thresholds", tree.useAutomaticThresholds);
                    if (autoThresholds != tree.useAutomaticThresholds)
                    {
                        Undo.RecordObject(tree, "Toggle Auto Thresholds");
                        tree.useAutomaticThresholds = autoThresholds;
                    }
                }
            }
        }

        private void SetParameterDefaultWarning(bool enabled)
        {
            if (this == null || _showParameterDefaultWarning == enabled) return;
            _showParameterDefaultWarning = enabled;
            EditorPrefs.SetBool(GetProjectScopedPreferenceKey(PrefParameterDefaultWarningKey), enabled);
            CancelParameterDrag();
            _paramScrollPos = Vector2.zero;
            _parameterRenderVersion = -1;
            _parameterListControllerVersion = _parameterListVrcVersion = -1;
            Repaint();
        }

        private void SetParameterDuplicateWarning(bool enabled)
        {
            if (this == null || _showParameterDuplicateWarning == enabled) return;
            _showParameterDuplicateWarning = enabled;
            EditorPrefs.SetBool(GetProjectScopedPreferenceKey(PrefParameterDuplicateWarningKey), enabled);
            CancelParameterDrag();
            _paramScrollPos = Vector2.zero;
            _parameterRenderVersion = -1;
            _parameterListControllerVersion = _parameterListVrcVersion = -1;
            Repaint();
        }

        private void DrawParameterManagerTools(AnimatorController controller)
        {
            if (!HubDropdownButtonWithId("ParameterManagerTools", new GUIContent("Tools ▾", "Parameter usage, missing parameters and unused parameter removal."), GUI.skin.button, GUILayout.Width(90))) return;
            GUI.FocusControl(null);
            var menu = new HubDropdownMenu(this);
            menu.AddItem(new GUIContent("Parameter Usage", "Show or hide the Parameter Usage panel."), _showParameterUsage,
                () => SetParameterUsageVisible(controller, !_showParameterUsage),
                selectedNow: () => _showParameterUsage,
                enabledNow: () => controller != null && controller == GetAnimatorController());
            menu.AddItem(new GUIContent("Missing Parameters", "Show or hide the missing parameter panel."),
                _showParameterManagerMissingParameters,
                () =>
                {
                    if (_showParameterManagerMissingParameters)
                    {
                        _showParameterManagerMissingParameters = false;
                        Repaint();
                    }
                    else EditorApplication.delayCall += () => { if (this != null) OpenParameterManagerMissingParameters(); };
                },
                selectedNow: () => _showParameterManagerMissingParameters,
                enabledNow: () => _showParameterManagerMissingParameters || CanEditParameterController());
            menu.AddItem(new GUIContent("Unused Parameters",
                "Show or hide the unused parameter panel."),
                _unusedParameterRemoval != null || _unusedParameterRemovalError != null,
                () =>
                {
                    if (_unusedParameterRemoval != null || _unusedParameterRemovalError != null)
                    {
                        _unusedParameterRemoval = null;
                        _unusedParameterRemovalError = null;
                        Repaint();
                    }
                    else EditorApplication.delayCall += () => { if (this != null) PreviewUnusedParameterRemoval(); };
                },
                selectedNow: () => _unusedParameterRemoval != null || _unusedParameterRemovalError != null,
                enabledNow: () => _unusedParameterRemoval != null || _unusedParameterRemovalError != null || CanEditParameterController());
            menu.ShowAsContext();
        }

        private void DrawParameterManagerOptions()
        {
            if (!HubDropdownButtonWithId("ParameterManagerOptions", new GUIContent("Options ▾", "Parameters Manager options, saved for this Unity project."), GUI.skin.button, GUILayout.Width(90))) return;
            GUI.FocusControl(null);
            var menu = new HubDropdownMenu(this);
            menu.AddItem(new GUIContent("Keep Out of VRC",
                "Keep built in Parameters out of VRC Parameters list."),
                _hideVrcBuiltInParameters, () =>
                {
                    _hideVrcBuiltInParameters = !_hideVrcBuiltInParameters;
                    EditorPrefs.SetBool(GetProjectScopedPreferenceKey(PrefHideVrcBuiltInParametersKey), _hideVrcBuiltInParameters);
                    // Apply the option before rebuilding the rows on the next layout.
                    _parameterListControllerVersion = _parameterListVrcVersion = -1;
                    Repaint();
                }, true, () => _hideVrcBuiltInParameters);
            menu.AddItem(new GUIContent("Duplicate Warning",
                "Duplicate Parameter Warning, Shows Parameters that are duped and fix options."),
                _showParameterDuplicateWarning, () =>
                {
                    GUI.FocusControl(null);
                    SetParameterDuplicateWarning(!_showParameterDuplicateWarning);
                },
                true, () => _showParameterDuplicateWarning);
            menu.AddItem(new GUIContent("Defaults Warning",
                "Parameter Defaults Warning, Shows Parameters that have different defaults and fix options."),
                _showParameterDefaultWarning, () =>
                {
                    GUI.FocusControl(null);
                    SetParameterDefaultWarning(!_showParameterDefaultWarning);
                },
                true, () => _showParameterDefaultWarning);
            menu.ShowAsContext();
        }

        private bool CanEditParameterController()
        {
            return GetAnimatorController() != null && !EditorApplication.isCompiling && !EditorApplication.isUpdating &&
                !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private void OpenParameterManagerMissingParameters()
        {
            if (!CanEditParameterController()) return;
            _showParameterManagerMissingParameters = true;
            _parameterManagerMissingScroll = Vector2.zero;
            CloseHubDropdown();
            RefreshSetupScan();
        }

        private void DrawParameterManagerMissingParameters()
        {
            if (!_showParameterManagerMissingParameters) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Missing Parameters", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(!CanEditParameterController()))
                if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                    EditorApplication.delayCall += () =>
                    {
                        if (this != null && _showParameterManagerMissingParameters && CanEditParameterController()) RefreshSetupScan();
                    };
            if (GUILayout.Button("Close", GUILayout.Width(60)))
            {
                _showParameterManagerMissingParameters = false;
                CloseHubDropdown();
            }
            EditorGUILayout.EndHorizontal();
            if (!_hasSetupScan || _setupTargetAtScan != _targetAsset || _setupParametersAtScan != _vrcExpressionParametersAsset ||
                _setupMenuAtScan != _vrcExpressionMenuAsset)
                GUILayout.Label("Press Refresh to check the currently loaded assets.", EditorStyles.wordWrappedMiniLabel);
            else
            {
                int count = 0;
                foreach (var issue in _setupIssues)
                    if (issue.missingParameters != null) count += issue.missingParameters.Count;
                if (count == 0) GUILayout.Label("No missing parameters found.", EditorStyles.miniLabel);
                else
                {
                    using (new NestedScrollViewScope(ref _parameterManagerMissingScroll,
                        GUILayout.Height(Mathf.Min(180f, count * (EditorGUIUtility.singleLineHeight + 4f) + 8f))))
                        for (int i = 0; i < _setupIssues.Count; i++)
                            if (_setupIssues[i].missingParameters != null)
                                DrawSetupMissingParameters(_setupIssues[i], "ParameterManagerMissing_" + i + "_");
                }
            }
            EditorGUILayout.EndVertical();
        }

        private static HashSet<string> CollectAnimatorParameterReferences(AnimatorController controller)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var hashNames = new Dictionary<int, List<string>>();
            foreach (var parameter in controller.parameters)
            {
                if (parameter == null || string.IsNullOrEmpty(parameter.name)) continue;
                int hash = Animator.StringToHash(parameter.name);
                if (!hashNames.TryGetValue(hash, out var names)) hashNames[hash] = names = new List<string>();
                names.Add(parameter.name);
            }
            void Add(string name) { if (!string.IsNullOrEmpty(name)) used.Add(name); }
            void AddHash(int hash)
            {
                if (hashNames.TryGetValue(hash, out var names)) foreach (var name in names) Add(name);
            }
            var inspected = new HashSet<UnityEngine.Object>();
            void Inspect(UnityEngine.Object asset, bool parameterFields = false)
            {
                if (asset == null || !inspected.Add(asset)) return;
                using (var serialized = new SerializedObject(asset))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType == SerializedPropertyType.ObjectReference &&
                            property.objectReferenceValue == null && property.objectReferenceInstanceIDValue != 0)
                            throw new InvalidOperationException("'" + asset.name + "' has a missing reference. Restore it before checking unused parameters.");
                        // Include custom behaviours as well as VRChat drivers. Keeping every matching
                        // serialized name/hash is conservative when a custom script's intent is unknown.
                        if (!parameterFields) continue;
                        if (property.propertyType == SerializedPropertyType.String) Add(property.stringValue);
                        else if (property.propertyType == SerializedPropertyType.Integer) AddHash(unchecked((int)property.longValue));
                    }
                }
            }
            void ScanBehaviours(StateMachineBehaviour[] behaviours)
            {
                if (behaviours == null) return;
                foreach (var behaviour in behaviours)
                {
                    if (behaviour == null) throw new InvalidOperationException("The controller has a missing behaviour. Restore it before checking unused parameters.");
                    Inspect(behaviour, true);
                }
            }
            void ScanTransition(AnimatorTransitionBase transition)
            {
                if (transition == null) throw new InvalidOperationException("The controller has a missing transition. Restore it before checking unused parameters.");
                foreach (var condition in transition.conditions) Add(condition.parameter);
            }
            var motions = new HashSet<Motion>();
            void ScanMotion(Motion motion)
            {
                if (motion == null || !motions.Add(motion)) return;
                if (motion is BlendTree tree)
                {
                    Inspect(tree);
                    if (tree.blendType != BlendTreeType.Direct)
                    {
                        Add(tree.blendParameter);
                        if (tree.blendType != BlendTreeType.Simple1D) Add(tree.blendParameterY);
                    }
                    foreach (var child in tree.children)
                    {
                        if (tree.blendType == BlendTreeType.Direct) Add(child.directBlendParameter);
                        ScanMotion(child.motion);
                    }
                }
                else if (motion is AnimationClip clip)
                {
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                        if (binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path)) Add(binding.propertyName);
                    foreach (var animationEvent in AnimationUtility.GetAnimationEvents(clip))
                    {
                        Add(animationEvent.stringParameter);
                        AddHash(animationEvent.intParameter);
                    }
                }
            }
            var layers = controller.layers;
            void ScanMachine(AnimatorStateMachine machine, int layer, HashSet<AnimatorStateMachine> visited)
            {
                if (machine == null) throw new InvalidOperationException("The controller has a missing state machine. Restore it before checking unused parameters.");
                if (!visited.Add(machine)) return;
                Inspect(machine);
                ScanBehaviours(machine.behaviours);
                foreach (var transition in machine.anyStateTransitions) ScanTransition(transition);
                foreach (var transition in machine.entryTransitions) ScanTransition(transition);
                foreach (var child in machine.states)
                {
                    var state = child.state;
                    if (state == null) throw new InvalidOperationException("The controller has a missing state. Restore it before checking unused parameters.");
                    Inspect(state);
                    if (state.speedParameterActive) Add(state.speedParameter);
                    if (state.timeParameterActive) Add(state.timeParameter);
                    if (state.mirrorParameterActive) Add(state.mirrorParameter);
                    if (state.cycleOffsetParameterActive) Add(state.cycleOffsetParameter);
                    foreach (var transition in state.transitions) ScanTransition(transition);
                    ScanBehaviours(state.behaviours);
                    ScanBehaviours(controller.GetStateEffectiveBehaviours(state, layer));
                    ScanMotion(state.motion);
                    // Walk each synced layer's override chain, including inherited overrides.
                    int source = layer;
                    var chain = new HashSet<int>();
                    while (source >= 0 && source < layers.Length && chain.Add(source) && layers[source].syncedLayerIndex >= 0)
                    {
                        ScanMotion(layers[source].GetOverrideMotion(state));
                        ScanBehaviours(controller.GetStateEffectiveBehaviours(state, source));
                        source = layers[source].syncedLayerIndex;
                    }
                }
                foreach (var child in machine.stateMachines)
                {
                    if (child.stateMachine == null) throw new InvalidOperationException("The controller has a missing state machine. Restore it before checking unused parameters.");
                    foreach (var transition in machine.GetStateMachineTransitions(child.stateMachine)) ScanTransition(transition);
                    ScanMachine(child.stateMachine, layer, visited);
                }
            }
            Inspect(controller);
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i] == null) throw new InvalidOperationException("The controller has a missing layer. Restore it before checking unused parameters.");
                var root = GetParameterUsageLayerRoot(controller, i);
                if (root == null) throw new InvalidOperationException("Cannot check layer '" + layers[i].name + "'. Its state machine or synced layer is missing or invalid.");
                ScanMachine(root, i, new HashSet<AnimatorStateMachine>());
            }
            return used;
        }

        private static UnusedParameterRemovalPlan BuildUnusedParameterRemovalPlan(AnimatorController controller, VRCExpressionParameters vrcAsset)
        {
            var used = CollectAnimatorParameterReferences(controller);
            var plan = new UnusedParameterRemovalPlan { controller = controller, vrcAsset = vrcAsset };
            var byName = new Dictionary<string, UnusedParameterRemovalRow>(StringComparer.Ordinal);
            foreach (var parameter in controller.parameters)
            {
                if (parameter == null || string.IsNullOrEmpty(parameter.name) || used.Contains(parameter.name)) continue;
                if (!byName.TryGetValue(parameter.name, out var row))
                {
                    row = new UnusedParameterRemovalRow { name = parameter.name };
                    byName.Add(row.name, row);
                    plan.rows.Add(row);
                }
                row.animatorTypes += (row.animatorCount++ > 0 ? ", " : "") + parameter.type;
                plan.animatorCount++;
            }
            if (vrcAsset != null && vrcAsset.parameters != null)
                foreach (var parameter in vrcAsset.parameters)
                    if (parameter != null && parameter.name != null && byName.TryGetValue(parameter.name, out var row))
                    {
                        row.vrcTypes += (row.vrcCount++ > 0 ? ", " : "") + parameter.valueType;
                        plan.vrcCount++;
                    }
            return plan;
        }

        private void PreviewUnusedParameterRemoval()
        {
            if (!CanEditParameterController()) return;
            CloseHubDropdown();
            _unusedParameterRemovalError = null;
            _unusedParameterRemoval = null;
            _unusedParameterRemovalScroll = Vector2.zero;
            try { _unusedParameterRemoval = BuildUnusedParameterRemovalPlan(GetAnimatorController(), _vrcExpressionParametersAsset); }
            catch (Exception exception) { _unusedParameterRemovalError = "Could not check all Animator references. Nothing was removed.\n" + exception.Message; }
            Repaint();
        }

        private void DrawUnusedParameterRemovalPreview()
        {
            var plan = _unusedParameterRemoval;
            if (plan == null && _unusedParameterRemovalError == null) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label("Remove Unused Parameters", EditorStyles.boldLabel);
            if (_unusedParameterRemovalError != null) EditorGUILayout.HelpBox(_unusedParameterRemovalError, MessageType.Warning);
            if (plan != null)
            {
                GUILayout.Label(plan.animatorCount + " Animator parameter(s) and " + plan.vrcCount + " matching VRC entry/entries will be removed.", EditorStyles.wordWrappedMiniLabel);
                GUILayout.Label("Usage is checked in the loaded Animator only. Menu and avatar references are not part of this check. Supports Undo.", EditorStyles.wordWrappedMiniLabel);
                if (plan.rows.Count == 0) EditorGUILayout.HelpBox("No unused Animator parameters found.", MessageType.Info);
                else
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label("Parameter", EditorStyles.boldLabel, GUILayout.MinWidth(100), GUILayout.ExpandWidth(true));
                    GUILayout.Label("Animator", EditorStyles.boldLabel, GUILayout.Width(105));
                    GUILayout.Label("VRC entries", EditorStyles.boldLabel, GUILayout.Width(150));
                    EditorGUILayout.EndHorizontal();
                    using (new NestedScrollViewScope(ref _unusedParameterRemovalScroll,
                        GUILayout.Height(Mathf.Min(180f, plan.rows.Count * (EditorGUIUtility.singleLineHeight + 4f) + 8f))))
                        foreach (var row in plan.rows)
                        {
                            EditorGUILayout.BeginHorizontal();
                            GUILayout.Label(new GUIContent(row.name, row.name), GUILayout.MinWidth(100), GUILayout.ExpandWidth(true));
                            GUILayout.Label(new GUIContent(row.animatorTypes, row.animatorTypes), GUILayout.Width(105));
                            GUILayout.Label(new GUIContent(row.vrcTypes ?? "—", row.vrcTypes ?? "No matching VRC entry"), GUILayout.Width(150));
                            EditorGUILayout.EndHorizontal();
                        }
                }
                if (plan.controller != GetAnimatorController() || plan.vrcAsset != _vrcExpressionParametersAsset)
                    EditorGUILayout.HelpBox("The loaded controller or VRC list changed. Refresh the preview before confirming.", MessageType.Info);
            }
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!CanEditParameterController() || plan == null || plan.rows.Count == 0 ||
                plan.controller != GetAnimatorController() || plan.vrcAsset != _vrcExpressionParametersAsset))
                if (GUILayout.Button("Confirm Remove", GUILayout.Width(125)))
                    EditorApplication.delayCall += () => { if (this != null) ConfirmUnusedParameterRemoval(plan); };
            using (new EditorGUI.DisabledScope(!CanEditParameterController()))
                if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                    EditorApplication.delayCall += () => { if (this != null) PreviewUnusedParameterRemoval(); };
            if (GUILayout.Button("Cancel", GUILayout.Width(65)))
            {
                _unusedParameterRemoval = null;
                _unusedParameterRemovalError = null;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void ConfirmUnusedParameterRemoval(UnusedParameterRemovalPlan preview)
        {
            if (!CanEditParameterController() || preview == null || preview != _unusedParameterRemoval ||
                preview.controller != GetAnimatorController() || preview.vrcAsset != _vrcExpressionParametersAsset) return;
            UnusedParameterRemovalPlan current;
            try { current = BuildUnusedParameterRemovalPlan(preview.controller, preview.vrcAsset); }
            catch (Exception exception)
            {
                _unusedParameterRemoval = null;
                _unusedParameterRemovalError = "Could not recheck all Animator references. Nothing was removed.\n" + exception.Message;
                Repaint();
                return;
            }
            bool same = current.rows.Count == preview.rows.Count;
            for (int i = 0; same && i < current.rows.Count; i++)
                same = current.rows[i].name == preview.rows[i].name && current.rows[i].animatorTypes == preview.rows[i].animatorTypes &&
                    current.rows[i].vrcTypes == preview.rows[i].vrcTypes;
            if (!same)
            {
                _unusedParameterRemoval = current;
                ShowNotification(new GUIContent("Parameters changed. Review the updated preview and confirm again."));
                Repaint();
                return;
            }
            if (current.rows.Count == 0) return;
            var names = new HashSet<string>(current.rows.ConvertAll(row => row.name), StringComparer.Ordinal);
            var controller = current.controller;
            var parameters = controller.parameters;
            string selectedName = _selectedParamIndex >= 0 && _selectedParamIndex < parameters.Length ? parameters[_selectedParamIndex]?.name : null;
            var remaining = new List<AnimatorControllerParameter>(parameters);
            remaining.RemoveAll(parameter => parameter != null && parameter.name != null && names.Contains(parameter.name));
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            const string undoName = "Remove Unused Parameters";
            Undo.SetCurrentGroupName(undoName);
            try
            {
                Undo.RegisterCompleteObjectUndo(controller, undoName);
                if (current.vrcAsset != null && current.vrcCount > 0) Undo.RegisterCompleteObjectUndo(current.vrcAsset, undoName);
                controller.parameters = remaining.ToArray();
                EditorUtility.SetDirty(controller);
                if (current.vrcAsset != null && current.vrcCount > 0)
                {
                    var vrcRemaining = new List<VRCExpressionParameters.Parameter>(current.vrcAsset.parameters);
                    vrcRemaining.RemoveAll(parameter => parameter != null && parameter.name != null && names.Contains(parameter.name));
                    current.vrcAsset.parameters = vrcRemaining.ToArray();
                    EditorUtility.SetDirty(current.vrcAsset);
                }
                Undo.FlushUndoRecordObjects();
                Undo.CollapseUndoOperations(undoGroup);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                _unusedParameterRemovalError = "Removal was cancelled and changes were undone.\n" + exception.Message;
                Repaint();
                return;
            }
            _unusedParameterRemoval = null;
            _unusedParameterRemovalError = null;
            _selectedParamIndex = remaining.FindIndex(parameter => parameter != null && parameter.name == selectedName);
            ResetParameterDrag();
            _parameterSerializedObject?.Dispose();
            _parameterSerializedObject = null;
            _parameterSerializedAsset = null;
            _parameterListControllerVersion = _parameterListVrcVersion = _parameterRenderVersion = -1;
            _parameterUsageScannedName = null;
            _parameterMergePlan = null;
            _hasParamCache = false;
            InvalidateLayerSummaries();
            AssetDatabase.SaveAssetIfDirty(controller);
            if (current.vrcAsset != null && current.vrcCount > 0) AssetDatabase.SaveAssetIfDirty(current.vrcAsset);
            if (_hasSetupScan) RefreshSetupScan();
            ShowNotification(new GUIContent("Removed " + current.animatorCount + " unused Animator parameter(s) and " + current.vrcCount + " VRC entry/entries."));
            Repaint();
        }

        private void SetParameterUsageVisible(AnimatorController controller, bool opened)
        {
            if (controller == null || controller != GetAnimatorController()) return;
            if (opened != _showParameterUsage)
            {
                _showParameterUsage = opened;
                _parameterUsageFollowedName = null;
                if (opened && string.IsNullOrEmpty(_parameterUsageName) && controller.parameters.Length > 0)
                    _parameterUsageName = controller.parameters[Mathf.Clamp(_selectedParamIndex, 0, controller.parameters.Length - 1)].name;
                if (!opened && _animationToolResizeIndex == ParameterUsageToolIndex) FinishAnimationToolResize();
                Repaint();
            }
        }

        private void DrawParameterUsagePanel(AnimatorController controller)
        {
            if (!_showParameterUsage)
            {
                if (_animationToolResizeIndex == ParameterUsageToolIndex) FinishAnimationToolResize();
                return;
            }

            // Update before laying out the results, and only scan when the selection changes.
            if (_parameterUsageFollowSelection && Event.current.type == EventType.Layout)
            {
                var parameters = controller.parameters;
                string selectedName = _selectedParamIndex >= 0 && _selectedParamIndex < parameters.Length
                    ? parameters[_selectedParamIndex].name : "";
                if (_parameterUsageFollowedController != controller || _parameterUsageFollowedName != selectedName)
                {
                    // Remember the attempt even if the scan fails, so it is not retried every repaint.
                    _parameterUsageFollowedController = controller;
                    _parameterUsageFollowedName = selectedName;
                    _parameterUsageName = selectedName;
                    ScanParameterUsage(controller);
                }
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            bool followSelection = EditorGUILayout.ToggleLeft(
                new GUIContent("Use Selected Parameter", "Automatically show usage for the parameter selected in Parameters Manager."),
                _parameterUsageFollowSelection);
            if (EditorGUI.EndChangeCheck())
            {
                _parameterUsageFollowSelection = followSelection;
                _parameterUsageFollowedName = null;
                EditorPrefs.SetBool(PrefParameterUsageFollowSelectionKey, followSelection);
                Repaint();
            }
            if (GUILayout.Button(new GUIContent("Close", "Close the Parameter Usage panel."), GUILayout.Width(60)))
            {
                SetParameterUsageVisible(controller, false);
                CloseHubDropdown();
            }
            EditorGUILayout.EndHorizontal();
            using (new EditorGUI.DisabledScope(_parameterUsageFollowSelection))
                _parameterUsageName = DrawParameterComboBoxWithLabel("ParameterUsageName", "Parameter", _parameterUsageName);
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_parameterUsageName)))
                if (GUILayout.Button("Find / Refresh Usage", GUILayout.Width(155))) ScanParameterUsage(controller);
            GUILayout.Label("Loaded controller, menus, parameters and Avatar Root. Refresh after edits.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            GUILayout.Label("Show", GUILayout.Width(35));
            _parameterUsageFilter = HubMaskField("ParameterUsageFilter", _parameterUsageFilter, ParameterUsageKindLabels, GUILayout.MinWidth(100));
            GUILayout.Label("Sort", GUILayout.Width(30));
            _parameterUsageSort = HubPopup("ParameterUsageSort", null, _parameterUsageSort, ParameterUsageSortLabels, GUILayout.Width(115));
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetInt(PrefParameterUsageFilterKey, _parameterUsageFilter);
                EditorPrefs.SetInt(PrefParameterUsageSortKey, _parameterUsageSort);
                _animationToolScroll[ParameterUsageToolIndex] = Vector2.zero;
                _parameterUsageViewDirty = true;
            }
            EditorGUILayout.EndHorizontal();
            bool current = _parameterUsageScannedName == _parameterUsageName && _parameterUsageController == controller &&
                _parameterUsageRoot == _avatarRoot && _parameterUsageMenu == _vrcExpressionMenuAsset && _parameterUsageParameters == _vrcExpressionParametersAsset;
            using (var scroll = new NestedScrollViewScope(ref _animationToolScroll[ParameterUsageToolIndex], GUILayout.Height(_animationToolHeights[ParameterUsageToolIndex])))
            {
                if (_parameterUsageFollowSelection && string.IsNullOrEmpty(_parameterUsageName))
                    EditorGUILayout.HelpBox("Select a parameter in Parameters Manager to see its usage here.", MessageType.Info);
                else if (!current) EditorGUILayout.HelpBox("Choose a parameter and click Find / Refresh Usage.", MessageType.Info);
                else
                {
                    if (_parameterUsageViewDirty)
                    {
                        _visibleParameterUsages.Clear();
                        foreach (var usage in _parameterUsages)
                            if ((_parameterUsageFilter & (1 << (int)usage.kind)) != 0) _visibleParameterUsages.Add(usage);
                        if (_parameterUsageSort != 0)
                            _visibleParameterUsages.Sort((a, b) =>
                            {
                                int category = _parameterUsageSort == 1 ? a.kind.CompareTo(b.kind) : 0;
                                return category != 0 ? category : StringComparer.OrdinalIgnoreCase.Compare(a.location, b.location);
                            });
                        _parameterUsageViewDirty = false;
                    }
                    EditorGUILayout.LabelField(_visibleParameterUsages.Count + " of " + _parameterUsages.Count + " matches shown", EditorStyles.boldLabel);
                    if (_parameterUsages.Count == 0) EditorGUILayout.HelpBox("No matches in the scanned assets. Custom scripts and build-time generated content are not included.", MessageType.Info);
                    else if (_visibleParameterUsages.Count == 0) EditorGUILayout.HelpBox("No matches for the selected usage types. Change Show to see other results.", MessageType.Info);
                    foreach (var usage in _visibleParameterUsages)
                    {
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField(usage.location, EditorStyles.wordWrappedMiniLabel);
                        using (new EditorGUI.DisabledScope(usage.target == null))
                            if (GUILayout.Button(new GUIContent("Show", usage.layer >= 0 ? "Open this location in the Animator and Inspector." : "Show this usage."), GUILayout.Width(48)))
                            {
                                var target = usage;
                                GUI.FocusControl(null);
                                EditorApplication.delayCall += () => { if (this != null && target.target != null) ShowParameterUsage(target); };
                            }
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }
            DrawAnimationToolResizer(ParameterUsageToolIndex);
            EditorGUILayout.EndVertical();
        }

        private void ShowParameterUsage(ParameterUsageEntry usage)
        {
            int navigationRequest = ++_parameterUsageAnimatorNavigation;
            if (usage.target == null) return;
            if (usage.target is Component component)
            {
                ShowSetupIssue(new SetupIssue { avatarComponents = new[] { component } });
                return;
            }
            if (usage.menuPath != null && usage.menuPath.Count > 0 && usage.menuPath[0] == _vrcExpressionMenuAsset)
            {
                var menu = usage.target as VRCExpressionsMenu;
                _showMenuEditor = true;
                _menuNavigationPath.Clear();
                _menuNavigationPath.AddRange(usage.menuPath);
                _menuSearchQuery = "";
                SetMenuSearchFilter(_menuSearchAll, -1);
                _menuScrollPos = Vector2.zero;
                if (menu != null && menu.controls != null) SelectMenuControl(menu, menu.controls.IndexOf(usage.menuControl));
            }
            int usageLayer = FindParameterUsageLayer(usage);
            if (usage.controller != null && usage.controller == GetAnimatorController() && usageLayer >= 0)
            {
                var layer = usage.controller.layers[usageLayer];
                _showLayerEditor = true;
                _selectedLayerIndex = usageLayer;
                _layerSelectionStateMachine = layer.stateMachine;
                _layerSelectionName = layer.name;
                _layerSearchQuery = "";
                ResetStateMachineNavigation();
                var path = new List<AnimatorStateMachine>();
                if (FindLayerStateMachinePath(usage.layerRoot, usage.machine, path, new HashSet<AnimatorStateMachine>()))
                    _stateMachineNavigation.AddRange(path);
                _selectedState = usage.state;
                _selectedTransition = usage.target as AnimatorStateTransition;
                if (_cachedBehaviourEditor != null) DestroyImmediate(_cachedBehaviourEditor);
                _cachedBehaviourEditor = null;
            }
            Selection.activeObject = usage.target;
            EditorGUIUtility.PingObject(usage.target);
            if (usageLayer >= 0) OpenParameterUsageAnimator(usage, usageLayer, navigationRequest);
            else if (usage.layer >= 0) ShowNotification(new GUIContent("This Animator location changed. Refresh Parameter Usage."));
            Repaint();
        }

        private static AnimatorStateMachine GetParameterUsageLayerRoot(AnimatorController controller, int layer)
        {
            if (controller == null || layer < 0) return null;
            var layers = controller.layers;
            var visited = new HashSet<int>();
            while (layer >= 0 && layer < layers.Length && visited.Add(layer))
            {
                if (layers[layer].syncedLayerIndex < 0) return layers[layer].stateMachine;
                layer = layers[layer].syncedLayerIndex;
            }
            return null;
        }

        private static int FindParameterUsageLayer(ParameterUsageEntry usage)
        {
            if (usage.controller == null || usage.layer < 0 || usage.layerRoot == null) return -1;
            var layers = usage.controller.layers;
            bool Matches(int index) => index >= 0 && index < layers.Length && layers[index].name == usage.layerName &&
                GetParameterUsageLayerRoot(usage.controller, index) == usage.layerRoot;
            if (Matches(usage.layer)) return usage.layer;
            for (int i = 0; i < layers.Length; i++) if (Matches(i)) return i;
            return -1;
        }

        private static bool FindParameterUsageBlendTreePath(BlendTree current, BlendTree target,
            List<BlendTree> path, HashSet<BlendTree> visited)
        {
            if (current == null || !visited.Add(current)) return false;
            path.Add(current);
            if (current == target) return true;
            foreach (var child in current.children)
                if (child.motion is BlendTree nested && FindParameterUsageBlendTreePath(nested, target, path, visited)) return true;
            path.RemoveAt(path.Count - 1);
            return false;
        }

        private const System.Reflection.BindingFlags ParameterUsageAnimatorFlags = System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

        private static object InvokeParameterUsageAnimator(object target, string method, params object[] arguments)
        {
            var member = target.GetType().GetMethod(method, ParameterUsageAnimatorFlags);
            if (member == null) throw new MissingMethodException(target.GetType().FullName, method);
            return member.Invoke(target, arguments);
        }

        private void OpenParameterUsageAnimator(ParameterUsageEntry usage, int layer, int request)
        {
            try
            {
                AssetDatabase.OpenAsset(usage.controller);
                var windowType = Type.GetType("UnityEditor.Graphs.AnimatorControllerTool, UnityEditor.Graphs");
                if (windowType == null) throw new InvalidOperationException("Animator navigation is unavailable.");
                var window = EditorWindow.GetWindow(windowType);
                window.Show();
                window.Focus();
                Selection.activeObject = usage.target;
                // Let a newly opened Animator finish creating its graph and layer views first.
                EditorApplication.delayCall += () =>
                {
                    if (this == null || window == null || request != _parameterUsageAnimatorNavigation || usage.target == null || usage.controller == null || Selection.activeObject != usage.target) return;
                    try
                    {
                        var machinePath = new List<AnimatorStateMachine>();
                        if (FindParameterUsageLayer(usage) != layer || !FindLayerStateMachinePath(usage.layerRoot, usage.machine, machinePath, new HashSet<AnimatorStateMachine>()))
                            throw new InvalidOperationException("The scanned location changed.");
                        var treePath = new List<BlendTree>();
                        if (usage.target is BlendTree tree)
                        {
                            var motion = GetSetupStateMotion(usage.controller, usage.state, layer);
                            if (!FindParameterUsageBlendTreePath(motion as BlendTree, tree, treePath, new HashSet<BlendTree>()))
                                throw new InvalidOperationException("The scanned BlendTree moved.");
                        }
                        // These navigation hooks are internal to Unity's Animator window.
                        // Reflect them so this tool does not require a reference to UnityEditor.Graphs.
                        var controllerProperty = windowType.GetProperty("animatorController", ParameterUsageAnimatorFlags);
                        var layerProperty = windowType.GetProperty("selectedLayerIndex", ParameterUsageAnimatorFlags);
                        if (controllerProperty == null || layerProperty == null) throw new MissingMemberException("Animator navigation properties are unavailable.");
                        controllerProperty.SetValue(window, usage.controller);
                        layerProperty.SetValue(window, layer);
                        InvokeParameterUsageAnimator(window, "BuildBreadCrumbsFromSMHierarchy", machinePath);
                        InvokeParameterUsageAnimator(window, "UpdateStateMachineSelection");
                        InvokeParameterUsageAnimator(window, "UpdateGraph");
                        if (treePath.Count > 0)
                        {
                            InvokeParameterUsageAnimator(window, "AddBreadCrumb", usage.state, false);
                            // The state breadcrumb already represents the root BlendTree.
                            // Synced motion overrides are framed within their full tree, since Unity
                            // validates nested breadcrumbs against the source state's original motion.
                            if (usage.state.motion == treePath[0])
                                for (int i = 1; i < treePath.Count; i++) InvokeParameterUsageAnimator(window, "AddBreadCrumb", treePath[i], false);
                            InvokeParameterUsageAnimator(window, "UpdateGraph");
                        }
                        window.Repaint();
                        // Frame after the Animator has laid out its viewport.
                        EditorApplication.delayCall += () =>
                        {
                            if (this == null || window == null || request != _parameterUsageAnimatorNavigation || usage.target == null || Selection.activeObject != usage.target) return;
                            try
                            {
                                var graphProperty = windowType.GetProperty("activeGraphGUI", ParameterUsageAnimatorFlags);
                                var graph = graphProperty != null ? graphProperty.GetValue(window) : null;
                                if (graph == null) throw new MissingMemberException("The Animator graph is unavailable.");
                                var graphTarget = usage.target is BlendTree || usage.target is AnimatorTransitionBase || usage.target is AnimatorState
                                    ? usage.target : (UnityEngine.Object)usage.state ?? usage.machine;
                                Selection.activeObject = graphTarget;
                                InvokeParameterUsageAnimator(graph, "SyncGraphToUnitySelection", true);
                                InvokeParameterUsageAnimator(window, "FrameSelection");
                                Selection.activeObject = usage.target;
                                window.Focus();
                                window.Repaint();
                            }
                            catch (Exception) { ShowParameterUsageNavigationFallback(usage); }
                        };
                    }
                    catch (Exception) { ShowParameterUsageNavigationFallback(usage); }
                };
            }
            catch (Exception) { ShowParameterUsageNavigationFallback(usage); }
        }

        private void ShowParameterUsageNavigationFallback(ParameterUsageEntry usage)
        {
            if (usage.target != null) Selection.activeObject = usage.target;
            ShowNotification(new GUIContent("Unity could not focus this Animator location. The item is selected in the Inspector; refresh usage and try again."));
        }

        private static ParameterUsageKind GetParameterUsageKind(UnityEngine.Object target)
        {
            if (target is AnimatorController) return ParameterUsageKind.AnimatorDefinition;
            if (target is VRCExpressionParameters) return ParameterUsageKind.VrcDefinition;
            if (target is AnimatorTransitionBase) return ParameterUsageKind.Transition;
            if (target is BlendTree) return ParameterUsageKind.BlendTree;
            if (target is StateMachineBehaviour) return ParameterUsageKind.Driver;
            if (target is AnimationClip) return ParameterUsageKind.Animation;
            if (target is VRCExpressionsMenu) return ParameterUsageKind.Menu;
            if (target is VRC.Dynamics.ContactBase) return ParameterUsageKind.Contact;
            if (target is VRC.Dynamics.VRCPhysBoneBase) return ParameterUsageKind.PhysBone;
            return ParameterUsageKind.StateSettings;
        }

        private void ScanParameterUsage(AnimatorController controller)
        {
            _parameterUsages.Clear();
            _visibleParameterUsages.Clear();
            _parameterUsageViewDirty = true;
            _parameterUsageScannedName = null;
            string name = _parameterUsageName;
            if (string.IsNullOrWhiteSpace(name) || controller == null) return;

            void Add(string parameter, string location, UnityEngine.Object target, int layer = -1,
                AnimatorStateMachine machine = null, AnimatorState state = null)
            {
                if (parameter != name || target == null) return;
                _parameterUsages.Add(new ParameterUsageEntry { kind = GetParameterUsageKind(target), location = location, target = target, controller = controller,
                    layer = layer, layerName = layer >= 0 ? controller.layers[layer].name : null,
                    layerRoot = GetParameterUsageLayerRoot(controller, layer), machine = machine, state = state });
            }
            void ScanConditions(AnimatorCondition[] conditions, UnityEngine.Object transition, string location, int layer, AnimatorStateMachine machine, AnimatorState state)
            {
                if (transition is AnimatorTransitionBase connection)
                    location += " → " + (connection.isExit ? "Exit" : connection.destinationState != null ? connection.destinationState.name :
                        connection.destinationStateMachine != null ? connection.destinationStateMachine.name : "Missing destination");
                foreach (var condition in conditions)
                    Add(condition.parameter, location + " — condition: " + condition.mode, transition, layer, machine, state);
            }
            void ScanBehaviours(StateMachineBehaviour[] behaviours, string location, int layer, AnimatorStateMachine machine, AnimatorState state)
            {
                if (behaviours == null) return;
                foreach (var behaviour in behaviours)
                {
                    if (!(behaviour is VRC.SDKBase.VRC_AvatarParameterDriver)) continue;
                    using (var serialized = new SerializedObject(behaviour))
                    {
                        var entries = serialized.FindProperty("parameters");
                        if (entries == null || !entries.isArray) continue;
                        for (int i = 0; i < entries.arraySize; i++)
                        {
                            var entry = entries.GetArrayElementAtIndex(i);
                            var destination = entry.FindPropertyRelative("name");
                            var source = entry.FindPropertyRelative("source");
                            var type = entry.FindPropertyRelative("type");
                            if (destination != null) Add(destination.stringValue, location + " — Parameter Driver destination #" + (i + 1), behaviour, layer, machine, state);
                            if (source != null && type != null && type.propertyType == SerializedPropertyType.Enum && type.enumValueIndex >= 0 &&
                                type.enumValueIndex < type.enumNames.Length && type.enumNames[type.enumValueIndex] == "Copy")
                                Add(source.stringValue, location + " — Parameter Driver copy source #" + (i + 1), behaviour, layer, machine, state);
                        }
                    }
                }
            }
            void ScanMotion(Motion motion, string location, int layer, AnimatorStateMachine machine, AnimatorState state, HashSet<Motion> visited)
            {
                if (motion == null || !visited.Add(motion)) return;
                if (motion is BlendTree tree)
                {
                    string treePath = location + " / " + tree.name;
                    if (tree.blendType != BlendTreeType.Direct)
                    {
                        Add(tree.blendParameter, treePath + " — BlendTree X", tree, layer, machine, state);
                        if (tree.blendType != BlendTreeType.Simple1D) Add(tree.blendParameterY, treePath + " — BlendTree Y", tree, layer, machine, state);
                    }
                    foreach (var child in tree.children)
                    {
                        if (tree.blendType == BlendTreeType.Direct) Add(child.directBlendParameter, treePath + " / " +
                            (child.motion != null ? child.motion.name : "Missing motion") + " — direct child weight", tree, layer, machine, state);
                        ScanMotion(child.motion, treePath, layer, machine, state, visited);
                    }
                }
                else if (motion is AnimationClip clip)
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                        if (binding.type == typeof(Animator) && string.IsNullOrEmpty(binding.path))
                            Add(binding.propertyName, location + " / " + clip.name + " — animated Animator parameter", clip, layer, machine, state);
            }
            void ScanMachine(AnimatorStateMachine machine, string location, int layer, HashSet<AnimatorStateMachine> visited)
            {
                if (machine == null || !visited.Add(machine)) return;
                ScanBehaviours(machine.behaviours, location, layer, machine, null);
                foreach (var transition in machine.anyStateTransitions)
                    if (transition != null) ScanConditions(transition.conditions, transition, location + " / Any State", layer, machine, null);
                foreach (var transition in machine.entryTransitions)
                    if (transition != null) ScanConditions(transition.conditions, transition, location + " / Entry", layer, machine, null);
                foreach (var child in machine.states)
                {
                    var state = child.state;
                    if (state == null) continue;
                    string statePath = location + " / " + state.name;
                    if (state.speedParameterActive) Add(state.speedParameter, statePath + " — speed", state, layer, machine, state);
                    if (state.timeParameterActive) Add(state.timeParameter, statePath + " — motion time", state, layer, machine, state);
                    if (state.mirrorParameterActive) Add(state.mirrorParameter, statePath + " — mirror", state, layer, machine, state);
                    if (state.cycleOffsetParameterActive) Add(state.cycleOffsetParameter, statePath + " — cycle offset", state, layer, machine, state);
                    foreach (var transition in state.transitions)
                        if (transition != null) ScanConditions(transition.conditions, transition, statePath + " / Transition", layer, machine, state);
                    ScanBehaviours(controller.GetStateEffectiveBehaviours(state, layer), statePath, layer, machine, state);
                    // A synced layer with no motion override inherits its source motion.
                    Motion effectiveMotion = controller.GetStateEffectiveMotion(state, layer);
                    if (effectiveMotion == null && controller.layers[layer].syncedLayerIndex >= 0) effectiveMotion = state.motion;
                    ScanMotion(effectiveMotion, statePath, layer, machine, state, new HashSet<Motion>());
                }
                foreach (var child in machine.stateMachines)
                {
                    if (child.stateMachine == null) continue;
                    foreach (var transition in machine.GetStateMachineTransitions(child.stateMachine))
                        if (transition != null) ScanConditions(transition.conditions, transition, location + " / " + child.stateMachine.name + " / Transition", layer, machine, null);
                    ScanMachine(child.stateMachine, location + " / " + child.stateMachine.name, layer, visited);
                }
            }
            void ScanMenu(VRCExpressionsMenu menu, List<VRCExpressionsMenu> path, HashSet<VRCExpressionsMenu> visited)
            {
                if (menu == null || !visited.Add(menu)) return;
                path.Add(menu);
                if (menu.controls != null)
                    foreach (var control in menu.controls)
                    {
                        if (control == null) continue;
                        void AddMenuParameter(string parameter, string role)
                        {
                            if (parameter != name) return;
                            _parameterUsages.Add(new ParameterUsageEntry { kind = ParameterUsageKind.Menu, target = menu, menuPath = new List<VRCExpressionsMenu>(path), menuControl = control,
                                location = "Menu " + string.Join(" / ", path.ConvertAll(item => item.name)) + " / " + control.name + " — " + role });
                        }
                        if (control.parameter != null) AddMenuParameter(control.parameter.name, "control parameter");
                        if (control.subParameters != null)
                            for (int i = 0; i < control.subParameters.Length; i++)
                                if (control.subParameters[i] != null) AddMenuParameter(control.subParameters[i].name, "puppet parameter " + (i + 1));
                        if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu) ScanMenu(control.subMenu, path, visited);
                    }
                path.RemoveAt(path.Count - 1);
            }
            try
            {
                foreach (var parameter in controller.parameters) Add(parameter.name, "Animator definition — " + parameter.type, controller);
                var layers = controller.layers;
                for (int i = 0; i < layers.Length; i++)
                {
                    int source = i;
                    var chain = new HashSet<int>();
                    while (source >= 0 && source < layers.Length && layers[source].syncedLayerIndex >= 0 && chain.Add(source)) source = layers[source].syncedLayerIndex;
                    if (source >= 0 && source < layers.Length && layers[source].syncedLayerIndex < 0)
                        ScanMachine(layers[source].stateMachine, "Layer " + i + ": " + layers[i].name, i, new HashSet<AnimatorStateMachine>());
                }
                if (_vrcExpressionParametersAsset != null && _vrcExpressionParametersAsset.parameters != null)
                    foreach (var parameter in _vrcExpressionParametersAsset.parameters)
                        if (parameter != null) Add(parameter.name, "VRC definition — " + parameter.valueType, _vrcExpressionParametersAsset);
                ScanMenu(_vrcExpressionMenuAsset, new List<VRCExpressionsMenu>(), new HashSet<VRCExpressionsMenu>());
                if (_avatarRoot != null)
                    foreach (var component in _avatarRoot.GetComponentsInChildren<Component>(true))
                    {
                        if (component == null || IsSetupEditorOnly(component)) continue;
                        bool physBone = component is VRC.Dynamics.VRCPhysBoneBase;
                        bool receiver = component is VRC.Dynamics.ContactBase && component.GetType().Name.Contains("Receiver");
                        if (!physBone && !receiver) continue;
                        using (var serialized = new SerializedObject(component))
                        {
                            var parameter = serialized.FindProperty("parameter");
                            if (parameter == null || parameter.propertyType != SerializedPropertyType.String || string.IsNullOrEmpty(parameter.stringValue)) continue;
                            string path = AnimationUtility.CalculateTransformPath(component.transform, _avatarRoot.transform);
                            if (receiver) Add(parameter.stringValue, "Avatar / " + path + " — Contact Receiver output", component);
                            if (physBone)
                                foreach (string suffix in new[] { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" })
                                    Add(parameter.stringValue + suffix, "Avatar / " + path + " — PhysBone output " + suffix, component);
                        }
                    }
                _parameterUsageScannedName = name;
                _parameterUsageController = controller;
                _parameterUsageRoot = _avatarRoot;
                _parameterUsageMenu = _vrcExpressionMenuAsset;
                _parameterUsageParameters = _vrcExpressionParametersAsset;
                _animationToolScroll[ParameterUsageToolIndex] = Vector2.zero;
            }
            catch (Exception exception)
            {
                _parameterUsages.Clear();
                EditorUtility.DisplayDialog("Parameter Usage", "Could not finish scanning.\n\n" + exception.Message, "OK");
            }
        }

        // Additional tools integrated with the hub's loaded assets and saved panel sizes.
        private void DrawAnimationToolSection(int index, string title, bool visible)
        {
            if (!visible)
            {
                if (_animationToolResizeIndex == index) FinishAnimationToolResize();
                return;
            }
            GUILayout.Space(5);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            int action;
            float panelHeight = Mathf.Max(_animationToolHeights[index], GetAnimationToolMinimumHeight(index));
            using (var scroll = new NestedScrollViewScope(ref _animationToolScroll[index], GUILayout.Height(panelHeight)))
            {
                if (index == 0) action = DrawBlendshapeMapperControls();
                else if (index == 1) action = DrawPathReplaceControls();
                else action = DrawMergerControls();
            }
            if (index == 3 && Event.current.type == EventType.Repaint)
            {
                Rect viewport = GUILayoutUtility.GetLastRect();
                _mergerViewportScreen = new Rect(GUIUtility.GUIToScreenPoint(viewport.position), viewport.size);
            }
            DrawAnimationToolResizer(index);
            EditorGUILayout.EndVertical();
            // Run asset changes after closing the GUI groups.
            if (action == 0) return;
            GUI.FocusControl(null);
            if (index == 0) RunBlendshapeMapper(action == 2);
            else if (index == 1) RunPathReplacement(action == 2);
            else if (action >= 7) RunVrcParameterMerge(action == 8);
            else if (action >= 5) RunAnimationMerge(action == 6);
            else if (action >= 3) RunControllerMerge(action == 4);
            else RunAnimatorLayerMerge(action == 2);
        }

        private int DrawMergerControls()
        {
            int action = 0;
            EditorGUI.BeginChangeCheck();
            _showAnimationMerger = EditorGUILayout.Foldout(_showAnimationMerger, "Animation Merger", true, EditorStyles.foldoutHeader);
            if (EditorGUI.EndChangeCheck()) EditorPrefs.SetBool(PrefShowAnimationMergerKey, _showAnimationMerger);
            if (_showAnimationMerger)
            {
                int animationAction = DrawAnimationMergerControls();
                if (animationAction != 0) action = animationAction + 4;
            }
            GUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            _layerMergerFoldout = EditorGUILayout.Foldout(_layerMergerFoldout, "Layer Merger", true, EditorStyles.foldoutHeader);
            if (EditorGUI.EndChangeCheck()) EditorPrefs.SetBool(PrefLayerMergerFoldoutKey, _layerMergerFoldout);
            if (_layerMergerFoldout)
            {
                int layerAction = DrawAnimatorLayerMergerControls();
                if (layerAction != 0) action = layerAction;
            }
            GUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            _controllerMergerFoldout = EditorGUILayout.Foldout(_controllerMergerFoldout, "Controller Merger", true, EditorStyles.foldoutHeader);
            if (EditorGUI.EndChangeCheck()) EditorPrefs.SetBool(PrefControllerMergerFoldoutKey, _controllerMergerFoldout);
            if (_controllerMergerFoldout)
            {
                int controllerAction = DrawControllerMergerControls();
                if (controllerAction != 0) action = controllerAction + 2;
            }
            GUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            _parameterMergerFoldout = EditorGUILayout.Foldout(_parameterMergerFoldout, "Parameter Merger", true, EditorStyles.foldoutHeader);
            if (EditorGUI.EndChangeCheck()) EditorPrefs.SetBool(PrefParameterMergerFoldoutKey, _parameterMergerFoldout);
            if (_parameterMergerFoldout)
            {
                int parameterAction = DrawVrcParameterMergerControls();
                if (parameterAction != 0) action = parameterAction + 6;
            }
            return action;
        }

        private int DrawVrcParameterMergerControls()
        {
            var target = _vrcExpressionParametersAsset;
            if (target == null)
            {
                EditorGUILayout.HelpBox("Assign the target list in the VRC Parameters slot above.", MessageType.Info);
                return 0;
            }
            EditorGUILayout.LabelField("Target: " + target.name, EditorStyles.miniLabel);
            EditorGUILayout.HelpBox("Merges the Source VRC Parameters to your VRC Parameters.", MessageType.Info);
            var sources = _parameterMergeSources.ToArray();
            Rect sourceArea = EditorGUILayout.BeginVertical();
            HandleVrcParameterMergeDrop(sourceArea, target);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Source VRC Lists", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            bool clear = GUILayout.Button("Clear All", GUILayout.Width(75));
            EditorGUILayout.EndHorizontal();
            int remove = -1;
            for (int i = 0; i < sources.Length; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                var source = (VRCExpressionParameters)EditorGUILayout.ObjectField(sources[i], typeof(VRCExpressionParameters), false);
                if (EditorGUI.EndChangeCheck()) { _parameterMergeSources[i] = source; _parameterMergePlan = null; }
                if (GUILayout.Button("X", GUILayout.Width(22))) remove = i;
                EditorGUILayout.EndHorizontal();
            }
            if (clear) { _parameterMergeSources.Clear(); _parameterMergePlan = null; }
            else if (remove >= 0) { _parameterMergeSources.RemoveAt(remove); _parameterMergePlan = null; }
            Rect dropRect = GUILayoutUtility.GetRect(0f, 45f, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, "Drop one or more VRC parameter lists here", EditorStyles.helpBox);
            var add = (VRCExpressionParameters)EditorGUILayout.ObjectField("Add Parameters", null, typeof(VRCExpressionParameters), false);
            if (add != null && !_parameterMergeSources.Contains(add)) { _parameterMergeSources.Add(add); _parameterMergePlan = null; }
            var plan = GetVrcParameterMergePlan();
            foreach (string error in plan.errors) EditorGUILayout.HelpBox(error, MessageType.Warning);
            if (plan.promotions.Count > 0)
                EditorGUILayout.HelpBox("Parameter conversions:\n" + string.Join("\n", plan.promotions), MessageType.Info);
            if (plan.sources.Count > 0)
            {
                EditorGUILayout.LabelField("Result: " + plan.parameters.Count + " parameters (" + plan.addedCount + " new), " +
                    plan.syncedBits + " / " + SetupSyncedParameterBitLimit + " synced bits. New defaults and settings come from the first source using that name.", EditorStyles.wordWrappedMiniLabel);
                if (plan.syncedBits > SetupSyncedParameterBitLimit)
                    EditorGUILayout.HelpBox("The merged list would be " + (plan.syncedBits - SetupSyncedParameterBitLimit) + " synced bits over budget.", MessageType.Warning);
            }
            int action = DrawAnimationToolActions("Check", "Merge Parameters", plan.sources.Count > 0 && plan.errors.Count == 0 && plan.parameters.Count > 0);
            EditorGUILayout.EndVertical();
            return action;
        }

        private void HandleVrcParameterMergeDrop(Rect sourceArea, VRCExpressionParameters target)
        {
            Event e = Event.current;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
            if (!Array.Exists(DragAndDrop.objectReferences, obj => obj is VRCExpressionParameters)) return;
            var dropArea = new Rect(GUIUtility.GUIToScreenPoint(sourceArea.position), sourceArea.size);
            dropArea.yMax = _mergerViewportScreen.yMax;
            Vector2 mouse = GUIUtility.GUIToScreenPoint(e.mousePosition);
            if (!dropArea.Contains(mouse) || !_mergerViewportScreen.Contains(mouse)) return;
            var additions = new List<VRCExpressionParameters>();
            foreach (var obj in DragAndDrop.objectReferences)
                if (obj is VRCExpressionParameters source && source != target && !_parameterMergeSources.Contains(source) && !additions.Contains(source))
                    additions.Add(source);
            DragAndDrop.visualMode = additions.Count > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (e.type == EventType.DragPerform && additions.Count > 0)
            {
                DragAndDrop.AcceptDrag();
                _parameterMergeSources.AddRange(additions);
                _parameterMergePlan = null;
                Repaint();
            }
            e.Use();
        }

        private static VRCExpressionParameters.Parameter CopyVrcMergeParameter(VRCExpressionParameters.Parameter parameter)
        {
            return new VRCExpressionParameters.Parameter
            {
                name = parameter.name, valueType = parameter.valueType, defaultValue = parameter.defaultValue,
                saved = parameter.saved, networkSynced = parameter.networkSynced
            };
        }

        private VrcParameterMergePlan GetVrcParameterMergePlan(bool force = false)
        {
            var target = _vrcExpressionParametersAsset;
            bool rebuild = force || _parameterMergePlan == null || _parameterMergePlan.target != target;
            if (!rebuild)
                foreach (var version in _parameterMergeVersions)
                    if (version.Key == null || EditorUtility.GetDirtyCount(version.Key) != version.Value) { rebuild = true; break; }
            if (!rebuild) return _parameterMergePlan;
            var plan = new VrcParameterMergePlan { target = target };
            _parameterMergeVersions.Clear();
            var assets = new List<VRCExpressionParameters>();
            if (target != null) assets.Add(target);
            foreach (var source in _parameterMergeSources)
            {
                if (source == null) continue;
                if (source == target) { plan.errors.Add("The target VRC list cannot also be a source."); continue; }
                if (plan.sources.Contains(source)) { plan.errors.Add("Source VRC list '" + source.name + "' is listed more than once."); continue; }
                plan.sources.Add(source);
                assets.Add(source);
            }
            foreach (var asset in assets)
            {
                _parameterMergeVersions[asset] = EditorUtility.GetDirtyCount(asset);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameter in asset.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>())
                {
                    if (parameter == null)
                    {
                        plan.errors.Add("VRC list '" + asset.name + "' contains a missing parameter entry.");
                        continue;
                    }
                    // Empty slots in a VRC list are placeholders, not named parameters to merge.
                    if (string.IsNullOrWhiteSpace(parameter.name)) continue;
                    if (!names.Add(parameter.name))
                    {
                        plan.errors.Add("VRC list '" + asset.name + "' has a duplicate parameter name: '" + parameter.name + "'.");
                        continue;
                    }
                    if (parameter.valueType != VRCExpressionParameters.ValueType.Bool &&
                        parameter.valueType != VRCExpressionParameters.ValueType.Int && parameter.valueType != VRCExpressionParameters.ValueType.Float)
                    {
                        plan.errors.Add("Parameter '" + parameter.name + "' in '" + asset.name + "' has an unsupported type.");
                        continue;
                    }
                    if (!plan.byName.TryGetValue(parameter.name, out var entry))
                    {
                        entry = new VrcParameterMergeEntry { original = CopyVrcMergeParameter(parameter), type = parameter.valueType };
                        plan.byName.Add(parameter.name, entry);
                        plan.parameters.Add(entry);
                        if (asset != target) plan.addedCount++;
                    }
                    if (!entry.owners.ContainsKey(parameter.valueType)) entry.owners.Add(parameter.valueType, asset.name);
                }
            }
            foreach (var entry in plan.parameters)
            {
                bool hasFloat = entry.owners.ContainsKey(VRCExpressionParameters.ValueType.Float);
                bool hasInt = entry.owners.ContainsKey(VRCExpressionParameters.ValueType.Int);
                bool hasBool = entry.owners.ContainsKey(VRCExpressionParameters.ValueType.Bool);
                if (hasFloat && hasInt)
                    plan.errors.Add("Cannot merge parameter '" + entry.original.name + "': Float in '" + entry.owners[VRCExpressionParameters.ValueType.Float] +
                        "' conflicts with Int in '" + entry.owners[VRCExpressionParameters.ValueType.Int] + "'.");
                else
                {
                    entry.type = hasFloat ? VRCExpressionParameters.ValueType.Float : hasInt ? VRCExpressionParameters.ValueType.Int : entry.original.valueType;
                    if (hasBool && (hasFloat || hasInt)) plan.promotions.Add(entry.original.name + ": Bool → " + entry.type);
                }
                if (entry.original.networkSynced)
                    plan.syncedBits += entry.type == VRCExpressionParameters.ValueType.Bool ? 1 : 8;
            }
            _parameterMergePlan = plan;
            return plan;
        }

        private void RunVrcParameterMerge(bool apply)
        {
            const string title = "Parameter Merger";
            var plan = GetVrcParameterMergePlan(true);
            var target = plan.target;
            try
            {
                if (target == null || plan.sources.Count == 0) throw new InvalidOperationException("Assign a target and at least one source VRC parameter list.");
                if (plan.errors.Count > 0) throw new InvalidOperationException(string.Join("\n", plan.errors));
                if (plan.parameters.Count == 0) throw new InvalidOperationException("These lists have no named parameters to merge.");
                string path = AssetDatabase.GetAssetPath(target);
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !AssetDatabase.IsOpenForEdit(path) ||
                    (System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) != 0)
                    throw new InvalidOperationException("Use an editable target VRC Parameters asset saved in this project's Assets folder.");
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog(title, "Cannot merge these VRC lists.\n\n" + exception.Message, "OK");
                return;
            }
            if (!apply)
            {
                EditorUtility.DisplayDialog(title, "Ready to merge " + plan.sources.Count + " source VRC list(s) into '" + target.name +
                    "'.\nResult: " + plan.parameters.Count + " parameters (" + plan.addedCount + " new), " + plan.syncedBits + " / " + SetupSyncedParameterBitLimit + " synced bits.\n\n" +
                    (plan.promotions.Count > 0 ? string.Join("\n", plan.promotions) + "\n\n" : "") +
                    (plan.syncedBits > SetupSyncedParameterBitLimit ? "Warning: the merged list exceeds the synced parameter budget.\n\n" : "") +
                    "Target defaults, Saved and Synced settings take priority. New parameters use the first source's settings. Source lists are preserved. The merge supports Undo.", "OK");
                return;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Merge VRC Parameters");
            string failure = null;
            try
            {
                // Build independent parameter records before assigning anything to the target.
                var parameters = new List<VRCExpressionParameters.Parameter>();
                foreach (var entry in plan.parameters)
                {
                    var parameter = CopyVrcMergeParameter(entry.original);
                    if (parameter.valueType != entry.type)
                        parameter.defaultValue = (float)ConvertParameterDefault(parameter.defaultValue,
                            GetAnimatorTypeForVrcType((int)parameter.valueType), GetAnimatorTypeForVrcType((int)entry.type));
                    parameter.valueType = entry.type;
                    parameters.Add(parameter);
                }
                Undo.RegisterCompleteObjectUndo(target, "Merge VRC Parameters");
                target.parameters = parameters.ToArray();
                target.isEmpty = false;
                EditorUtility.SetDirty(target);
                Undo.FlushUndoRecordObjects();
                AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);
            }
            catch (Exception exception)
            {
                failure = "The merge was rolled back.\n\n" + exception.Message;
                try
                {
                    Undo.FlushUndoRecordObjects();
                    Undo.RevertAllDownToGroup(undoGroup);
                    AssetDatabase.SaveAssets();
                }
                catch (Exception rollbackException)
                {
                    failure = "The merge failed and could not be fully rolled back.\n\n" + exception.Message + "\n\n" + rollbackException.Message;
                }
            }
            finally
            {
                _parameterMergePlan = null;
                _parameterListControllerVersion = _parameterListVrcVersion = -1;
                _parameterUsageScannedName = null;
                _hasParamCache = false;
                _nextMenuSearchRefresh = 0;
                if (_hasSetupScan) _setupChecksStale = true;
                Repaint();
            }
            if (failure != null)
            {
                EditorUtility.DisplayDialog(title, failure, "OK");
                return;
            }
            _parameterMergeSources.Clear();
            ShowNotification(new GUIContent("Merged " + plan.sources.Count + " VRC list(s): " + plan.addedCount + " new parameters."));
        }

        private int DrawControllerMergerControls()
        {
            var target = GetAnimatorController();
            if (target == null)
            {
                EditorGUILayout.HelpBox("Assign the target FX controller in the Animation Controller slot above.", MessageType.Info);
                return 0;
            }
            EditorGUILayout.LabelField("Target: " + target.name, EditorStyles.miniLabel);
            EditorGUILayout.HelpBox("Merge the Source Controllers to your FX Controller.", MessageType.Info);
            // Keep this event's rows stable when a drop adds several controllers at once.
            var sources = _controllerMergeSources.ToArray();
            Rect sourceArea = EditorGUILayout.BeginVertical();
            HandleControllerMergeDrop(sourceArea, target);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Source Controllers", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            bool clear = GUILayout.Button("Clear All", GUILayout.Width(75));
            EditorGUILayout.EndHorizontal();
            int remove = -1;
            for (int i = 0; i < sources.Length; i++)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                var source = (AnimatorController)EditorGUILayout.ObjectField(sources[i], typeof(AnimatorController), false);
                if (EditorGUI.EndChangeCheck()) { _controllerMergeSources[i] = source; _controllerMergePlan = null; }
                if (GUILayout.Button("X", GUILayout.Width(22))) remove = i;
                EditorGUILayout.EndHorizontal();
            }
            if (clear) { _controllerMergeSources.Clear(); _controllerMergePlan = null; }
            else if (remove >= 0) { _controllerMergeSources.RemoveAt(remove); _controllerMergePlan = null; }
            Rect dropRect = GUILayoutUtility.GetRect(0f, 45f, GUILayout.ExpandWidth(true));
            GUI.Box(dropRect, "Drop one or more FX controllers here", EditorStyles.helpBox);
            var add = (AnimatorController)EditorGUILayout.ObjectField("Add Controller", null, typeof(AnimatorController), false);
            if (add != null && !_controllerMergeSources.Contains(add)) { _controllerMergeSources.Add(add); _controllerMergePlan = null; }
            var plan = GetControllerMergePlan();
            foreach (string error in plan.errors) EditorGUILayout.HelpBox(error, MessageType.Warning);
            if (plan.promotions.Count > 0)
                EditorGUILayout.HelpBox("Parameter conversions:\n" + string.Join("\n", plan.promotions), MessageType.Info);
            if (plan.sources.Count > 0)
                EditorGUILayout.LabelField("Result: " + plan.layerCount + " layers, " + plan.parameters.Count + " parameters. New parameter defaults come from the first source using that name.", EditorStyles.wordWrappedMiniLabel);
            int action = DrawAnimationToolActions("Check", "Merge Controllers", plan.sources.Count > 0 && plan.errors.Count == 0);
            EditorGUILayout.EndVertical();
            return action;
        }

        private void HandleControllerMergeDrop(Rect sourceArea, AnimatorController target)
        {
            Event e = Event.current;
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
            if (!Array.Exists(DragAndDrop.objectReferences, obj => obj is AnimatorController)) return;

            // Include the rows, picker and unused space down to the resize bar. Clip to the
            // visible scroll viewport so content outside the panel cannot accept a drop.
            var dropArea = new Rect(GUIUtility.GUIToScreenPoint(sourceArea.position), sourceArea.size);
            dropArea.yMax = _mergerViewportScreen.yMax;
            Vector2 mouse = GUIUtility.GUIToScreenPoint(e.mousePosition);
            if (!dropArea.Contains(mouse) || !_mergerViewportScreen.Contains(mouse)) return;

            // Run before ObjectField controls, which would otherwise accept only one source.
            var additions = new List<AnimatorController>();
            foreach (var obj in DragAndDrop.objectReferences)
                if (obj is AnimatorController source && source != target && !_controllerMergeSources.Contains(source) && !additions.Contains(source))
                    additions.Add(source);
            DragAndDrop.visualMode = additions.Count > 0 ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (e.type == EventType.DragPerform && additions.Count > 0)
            {
                DragAndDrop.AcceptDrag();
                _controllerMergeSources.AddRange(additions);
                _controllerMergePlan = null;
                Repaint();
            }
            e.Use();
        }

        private ControllerMergePlan GetControllerMergePlan(bool force = false)
        {
            var target = GetAnimatorController();
            bool rebuild = force || _controllerMergePlan == null || _controllerMergePlan.target != target;
            if (!rebuild)
                foreach (var version in _controllerMergeVersions)
                    if (version.Key == null || EditorUtility.GetDirtyCount(version.Key) != version.Value) { rebuild = true; break; }
            if (!rebuild) return _controllerMergePlan;
            var plan = new ControllerMergePlan { target = target };
            _controllerMergeVersions.Clear();
            var controllers = new List<AnimatorController>();
            if (target != null) controllers.Add(target);
            foreach (var source in _controllerMergeSources)
            {
                if (source == null) continue;
                if (source == target) { plan.errors.Add("The target controller cannot also be a source."); continue; }
                if (plan.sources.Contains(source)) { plan.errors.Add("Source controller '" + source.name + "' is listed more than once."); continue; }
                plan.sources.Add(source);
                controllers.Add(source);
            }
            foreach (var controller in controllers)
            {
                _controllerMergeVersions[controller] = EditorUtility.GetDirtyCount(controller);
                plan.layerCount += controller.layers.Length;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameter in controller.parameters)
                {
                    if (string.IsNullOrEmpty(parameter.name) || !names.Add(parameter.name))
                    {
                        plan.errors.Add("Controller '" + controller.name + "' has an empty or duplicate parameter name: '" + parameter.name + "'.");
                        continue;
                    }
                    if (!plan.byName.TryGetValue(parameter.name, out var entry))
                    {
                        entry = new ControllerMergeParameter
                        {
                            original = new AnimatorControllerParameter
                            {
                                name = parameter.name, type = parameter.type, defaultBool = parameter.defaultBool,
                                defaultInt = parameter.defaultInt, defaultFloat = parameter.defaultFloat
                            },
                            type = parameter.type
                        };
                        plan.byName.Add(parameter.name, entry);
                        plan.parameters.Add(entry);
                    }
                    if (!entry.owners.ContainsKey(parameter.type)) entry.owners.Add(parameter.type, controller.name);
                }
            }
            foreach (var entry in plan.parameters)
            {
                bool hasFloat = entry.owners.ContainsKey(AnimatorControllerParameterType.Float);
                bool hasInt = entry.owners.ContainsKey(AnimatorControllerParameterType.Int);
                bool hasBool = entry.owners.ContainsKey(AnimatorControllerParameterType.Bool);
                bool hasTrigger = entry.owners.ContainsKey(AnimatorControllerParameterType.Trigger);
                if (hasFloat && hasInt)
                {
                    plan.errors.Add("Cannot merge parameter '" + entry.original.name + "': Float in '" + entry.owners[AnimatorControllerParameterType.Float] +
                        "' conflicts with Int in '" + entry.owners[AnimatorControllerParameterType.Int] + "'.");
                    continue;
                }
                if (hasTrigger && entry.owners.Count > 1)
                {
                    plan.errors.Add("Cannot merge parameter '" + entry.original.name + "': Trigger conflicts with another parameter type.");
                    continue;
                }
                entry.type = hasFloat ? AnimatorControllerParameterType.Float : hasInt ? AnimatorControllerParameterType.Int : entry.original.type;
                if (hasBool && (hasFloat || hasInt)) plan.promotions.Add(entry.original.name + ": Bool → " + entry.type + " (transition conditions updated)");
            }
            _controllerMergePlan = plan;
            return plan;
        }

        private static bool IsControllerMergeGraphObject(UnityEngine.Object obj)
        {
            return obj is AnimatorStateMachine || obj is AnimatorState || obj is AnimatorTransitionBase ||
                obj is BlendTree || obj is StateMachineBehaviour;
        }

        private static List<UnityEngine.Object> CollectControllerMergeGraph(AnimatorController controller)
        {
            var objects = new List<UnityEngine.Object>();
            var visited = new HashSet<UnityEngine.Object>();
            var layers = controller.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                var dependencies = new HashSet<int>();
                for (int index = i; index >= 0; index = layers[index].syncedLayerIndex)
                {
                    if (index >= layers.Length || !dependencies.Add(index))
                        throw new InvalidOperationException("Controller '" + controller.name + "' has an invalid sync dependency on layer '" + layers[i].name + "'.");
                }
                if (layers[i].stateMachine != null) CollectLayerMergeObjects(layers[i].stateMachine, objects, visited, null);
                else if (layers[i].syncedLayerIndex < 0)
                    throw new InvalidOperationException("Controller '" + controller.name + "' has a missing state machine on layer '" + layers[i].name + "'.");
            }
            // Also follow serialized graph references: this includes synced-layer motion/behaviour overrides
            // and custom behaviour links to states in another layer. Clips and masks remain shared assets.
            for (int i = -1; i < objects.Count; i++)
            {
                using (var serialized = new SerializedObject(i < 0 ? (UnityEngine.Object)controller : objects[i]))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                        if (property.propertyType == SerializedPropertyType.ObjectReference && IsControllerMergeGraphObject(property.objectReferenceValue))
                            CollectLayerMergeObjects(property.objectReferenceValue, objects, visited, null);
                }
            }
            return objects;
        }

        private static void RemapControllerMergeReferences(UnityEngine.Object obj, Dictionary<UnityEngine.Object, UnityEngine.Object> copies)
        {
            using (var serialized = new SerializedObject(obj))
            {
                var property = serialized.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null &&
                        copies.TryGetValue(property.objectReferenceValue, out var replacement)) property.objectReferenceValue = replacement;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static bool IsFxLayerControl(SerializedObject serialized)
        {
            var playable = serialized.FindProperty("playable");
            return playable != null && playable.propertyType == SerializedPropertyType.Enum && playable.enumValueIndex >= 0 &&
                playable.enumValueIndex < playable.enumNames.Length && playable.enumNames[playable.enumValueIndex] == "FX";
        }

        private static void OffsetControllerMergeLayerControl(UnityEngine.Object obj, int offset, int sourceLayerCount)
        {
            if (!(obj is StateMachineBehaviour) || obj.GetType().Name != "VRCAnimatorLayerControl") return;
            using (var serialized = new SerializedObject(obj))
            {
                if (!IsFxLayerControl(serialized)) return;
                var layer = serialized.FindProperty("layer");
                if (layer == null || layer.propertyType != SerializedPropertyType.Integer || layer.intValue < 0 || layer.intValue >= sourceLayerCount)
                    throw new InvalidOperationException("An FX Animator Layer Control has an invalid layer number. Correct it before merging.");
                layer.intValue += offset;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static Dictionary<UnityEngine.Object, UnityEngine.Object> CopyControllerMergeGraph(AnimatorController source,
            AnimatorController target, List<UnityEngine.Object> originals, List<UnityEngine.Object> created, bool recordUndo)
        {
            var copies = new Dictionary<UnityEngine.Object, UnityEngine.Object> { { source, target } };
            foreach (var original in originals)
            {
                var copy = CreateLayerMergeObject(original);
                created.Add(copy);
                EditorUtility.CopySerialized(original, copy);
                copy.name = original.name;
                copy.hideFlags = HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(copy, target);
                if (recordUndo) Undo.RegisterCreatedObjectUndo(copy, "Merge Animator Controllers");
                copies.Add(original, copy);
            }
            foreach (var original in originals)
            {
                var copy = copies[original];
                RemapControllerMergeReferences(copy, copies);
                EditorUtility.SetDirty(copy);
            }
            return copies;
        }

        private static AnimatorController CreateControllerMergeDuplicate(AnimatorController source,
            List<UnityEngine.Object> originals, List<string> temporaryPaths)
        {
            string path = AssetDatabase.GenerateUniqueAssetPath("Assets/__AvatarControlHub_Merge_" + Guid.NewGuid().ToString("N") + ".controller");
            var duplicate = new AnimatorController();
            var created = new List<UnityEngine.Object> { duplicate };
            temporaryPaths.Add(path);
            try
            {
                AssetDatabase.CreateAsset(duplicate, path);
                EditorUtility.CopySerialized(source, duplicate);
                duplicate.name = source.name;
                duplicate.hideFlags = HideFlags.None;
                var copies = CopyControllerMergeGraph(source, duplicate, originals, created, false);
                RemapControllerMergeReferences(duplicate, copies);
                EditorUtility.SetDirty(duplicate);
                return duplicate;
            }
            catch
            {
                // Persistent objects are removed with their temporary asset in the caller's finally block.
                foreach (var obj in created)
                    if (obj != null && !EditorUtility.IsPersistent(obj)) DestroyImmediate(obj);
                throw;
            }
        }

        private static AnimatorControllerLayer[] CopyControllerMergeLayers(AnimatorController source, AnimatorController target,
            List<UnityEngine.Object> originals, int layerOffset, HashSet<string> layerNames, List<UnityEngine.Object> created)
        {
            var copies = CopyControllerMergeGraph(source, target, originals, created, true);
            foreach (var original in originals)
            {
                var copy = copies[original];
                OffsetControllerMergeLayerControl(copy, layerOffset, source.layers.Length);
                EditorUtility.SetDirty(copy);
            }
            // Copy the layer table through a temporary controller to retain all override tables and settings.
            var temporary = new AnimatorController();
            try
            {
                EditorUtility.CopySerialized(source, temporary);
                temporary.hideFlags = HideFlags.HideAndDontSave;
                RemapControllerMergeReferences(temporary, copies);
                var layers = temporary.layers;
                for (int i = 0; i < layers.Length; i++)
                {
                    string originalName = layers[i].name;
                    string uniqueName = originalName;
                    for (int suffix = 2; !layerNames.Add(uniqueName); suffix++) uniqueName = originalName + " (" + suffix + ")";
                    layers[i].name = uniqueName;
                    if (layers[i].syncedLayerIndex >= 0) layers[i].syncedLayerIndex += layerOffset;
                    // A controller's base layer always runs at full weight. Preserve that when appending it.
                    if (i == 0 && layerOffset > 0) layers[i].defaultWeight = 1f;
                }
                return layers;
            }
            finally { DestroyImmediate(temporary); }
        }

        private void RunControllerMerge(bool apply)
        {
            const string title = "Controller Merger";
            var plan = GetControllerMergePlan(true);
            var target = plan.target;
            var graphs = new Dictionary<AnimatorController, List<UnityEngine.Object>>();
            try
            {
                if (target == null || plan.sources.Count == 0) throw new InvalidOperationException("Assign a target and at least one source controller.");
                if (plan.errors.Count > 0) throw new InvalidOperationException(string.Join("\n", plan.errors));
                string path = AssetDatabase.GetAssetPath(target);
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !AssetDatabase.IsOpenForEdit(path) ||
                    (System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) != 0)
                    throw new InvalidOperationException("Use an editable target controller saved in this project's Assets folder.");
                graphs.Add(target, CollectControllerMergeGraph(target));
                foreach (var source in plan.sources) graphs.Add(source, CollectControllerMergeGraph(source));
                var sourceObjects = new HashSet<UnityEngine.Object>();
                foreach (var source in plan.sources) sourceObjects.UnionWith(graphs[source]);
                var convertedTargetParameters = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameter in target.parameters)
                    if (parameter.type != plan.byName[parameter.name].type) convertedTargetParameters.Add(parameter.name);
                foreach (var obj in graphs[target])
                    if (obj is AnimatorTransitionBase transition && (AssetDatabase.GetAssetPath(obj) != path || sourceObjects.Contains(obj)))
                        foreach (var condition in transition.conditions)
                            if (convertedTargetParameters.Contains(condition.parameter))
                                throw new InvalidOperationException("The target shares a transition with another controller for parameter '" + condition.parameter + "'. Give the target its own state machine before converting that shared parameter.");
                foreach (var source in plan.sources)
                    foreach (var obj in graphs[source])
                        if (obj is StateMachineBehaviour && obj.GetType().Name == "VRCAnimatorLayerControl")
                            using (var serialized = new SerializedObject(obj))
                                if (IsFxLayerControl(serialized))
                                {
                                    var layer = serialized.FindProperty("layer");
                                    if (layer == null || layer.intValue < 0 || layer.intValue >= source.layers.Length)
                                        throw new InvalidOperationException("Controller '" + source.name + "' has an FX Layer Control pointing outside its layer list.");
                                }
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog(title, "Cannot merge these controllers.\n\n" + exception.Message, "OK");
                return;
            }
            if (!apply)
            {
                EditorUtility.DisplayDialog(title, "Ready to merge " + plan.sources.Count + " source controller(s) into '" + target.name +
                    "'.\nResult: " + plan.layerCount + " layers and " + plan.parameters.Count + " parameters.\n\n" +
                    (plan.promotions.Count > 0 ? string.Join("\n", plan.promotions) + "\n\n" : "") +
                    "Each source is duplicated, converted, and merged; its temporary copy is then deleted. Target defaults take priority. The merge supports Undo.", "OK");
                return;
            }
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Merge Animator Controllers");
            var created = new List<UnityEngine.Object>();
            var temporaryPaths = new List<string>();
            var cleanupErrors = new List<string>();
            string failure = null;
            try
            {
                Undo.RegisterCompleteObjectUndo(target, "Merge Animator Controllers");
                var layers = new List<AnimatorControllerLayer>(target.layers);
                var layerNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var layer in layers) layerNames.Add(layer.name);
                // Convert only temporary source assets, then give the target its own copies of their graphs.
                foreach (var source in plan.sources)
                {
                    var duplicate = CreateControllerMergeDuplicate(source, graphs[source], temporaryPaths);
                    foreach (var parameter in duplicate.parameters)
                    {
                        var resolvedType = plan.byName[parameter.name].type;
                        if (parameter.type != resolvedType)
                            ConvertParameterType(duplicate, parameter.name, parameter.type, resolvedType, false, false, false);
                    }
                    layers.AddRange(CopyControllerMergeLayers(duplicate, target, CollectControllerMergeGraph(duplicate), layers.Count, layerNames, created));
                }
                foreach (var parameter in target.parameters)
                {
                    var resolvedType = plan.byName[parameter.name].type;
                    if (parameter.type != resolvedType)
                        ConvertParameterType(target, parameter.name, parameter.type, resolvedType, false, false);
                }
                var parameters = new List<AnimatorControllerParameter>();
                foreach (var entry in plan.parameters)
                {
                    var original = entry.original;
                    var parameter = new AnimatorControllerParameter
                    {
                        name = original.name, type = entry.type, defaultBool = original.defaultBool,
                        defaultInt = original.defaultInt, defaultFloat = original.defaultFloat
                    };
                    if (original.type == AnimatorControllerParameterType.Bool)
                    {
                        if (entry.type == AnimatorControllerParameterType.Float) parameter.defaultFloat = original.defaultBool ? 1f : 0f;
                        else if (entry.type == AnimatorControllerParameterType.Int) parameter.defaultInt = original.defaultBool ? 1 : 0;
                    }
                    parameters.Add(parameter);
                }
                target.parameters = parameters.ToArray();
                target.layers = layers.ToArray();
                EditorUtility.SetDirty(target);
                Undo.FlushUndoRecordObjects();
                AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);
            }
            catch (Exception exception)
            {
                failure = "The merge was rolled back.\n\n" + exception.Message;
                try
                {
                    Undo.FlushUndoRecordObjects();
                    Undo.RevertAllDownToGroup(undoGroup);
                    foreach (var obj in created) if (obj != null) DestroyImmediate(obj, true);
                    AssetDatabase.SaveAssets();
                }
                catch (Exception rollbackException)
                {
                    failure = "The merge failed and could not be fully rolled back.\n\n" + exception.Message + "\n\n" + rollbackException.Message;
                }
            }
            finally
            {
                foreach (string path in temporaryPaths)
                {
                    try
                    {
                        if ((System.IO.File.Exists(path) || System.IO.File.Exists(path + ".meta")) && !AssetDatabase.DeleteAsset(path))
                            cleanupErrors.Add(path);
                    }
                    catch (Exception exception) { cleanupErrors.Add(path + ": " + exception.Message); }
                }
                _controllerMergePlan = null;
            }
            string cleanupMessage = cleanupErrors.Count > 0 ? "\n\nCould not delete these temporary copies:\n" + string.Join("\n", cleanupErrors) : "";
            if (failure != null)
            {
                EditorUtility.DisplayDialog(title, failure + cleanupMessage, "OK");
                return;
            }
            _controllerMergeSources.Clear();
            _controllerMergePlan = null;
            _parameterListControllerVersion = _parameterListVrcVersion = -1;
            _parameterUsageScannedName = null;
            _animationToolClipsDirty = true;
            _hasParamCache = false;
            InvalidateLayerSummaries();
            RefreshAvailableTrees();
            if (_hasSetupScan) _setupChecksStale = true;
            Repaint();
            EditorUtility.DisplayDialog(title, "Merged " + plan.sources.Count + " controller(s) into '" + target.name + "'. You can undo this merge." + cleanupMessage, "OK");
        }

        private int DrawAnimatorLayerMergerControls()
        {
            AnimatorController controller = GetAnimatorController();
            if (_layerMergerController != controller)
            {
                _layerMergerController = controller;
                _layerMergeTarget = null;
                _layerMergeSources.Clear();
                _layerMergeSharedName = "";
                _layerMergeRemoveSources = false;
            }
            if (controller == null)
            {
                EditorGUILayout.HelpBox("Assign an Animator Controller in the top asset slots to merge its layers.", MessageType.Info);
                return 0;
            }
            var layers = controller.layers;
            if (layers.Length < 2)
            {
                EditorGUILayout.HelpBox("The loaded controller needs at least two layers to merge.", MessageType.Info);
                return 0;
            }
            int targetIndex = Array.FindIndex(layers, layer => layer.stateMachine == _layerMergeTarget);
            if (targetIndex < 0) targetIndex = 0;
            var names = new string[layers.Length];
            var available = new HashSet<AnimatorStateMachine>();
            for (int i = 0; i < layers.Length; i++)
            {
                names[i] = i + ": " + layers[i].name + (layers[i].syncedLayerIndex >= 0 ? " (synced)" : "");
                if (layers[i].stateMachine != null && layers[i].syncedLayerIndex < 0) available.Add(layers[i].stateMachine);
            }
            _layerMergeSources.RemoveWhere(machine => machine == null || !available.Contains(machine));
            EditorGUILayout.LabelField("Controller: " + controller.name, EditorStyles.miniLabel);
            EditorGUILayout.HelpBox("Merges the states that share the same name in to one copying all other states and transtions in to the base layer as well.", MessageType.Info);
            targetIndex = HubPopup("LayerMergeTarget_" + controller.GetInstanceID(), "Target Layer", targetIndex, names);
            _layerMergeTarget = layers[targetIndex].stateMachine;
            _layerMergeSources.Remove(_layerMergeTarget);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Source Layers", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("All", GUILayout.Width(45)))
                foreach (var machine in available) if (machine != _layerMergeTarget) _layerMergeSources.Add(machine);
            if (GUILayout.Button("None", GUILayout.Width(50))) _layerMergeSources.Clear();
            EditorGUILayout.EndHorizontal();
            for (int i = 0; i < layers.Length; i++)
            {
                if (i == targetIndex) continue;
                var machine = layers[i].stateMachine;
                bool enabled = machine != null && machine != _layerMergeTarget && layers[i].syncedLayerIndex < 0;
                using (new EditorGUI.DisabledScope(!enabled))
                {
                    bool selected = enabled && _layerMergeSources.Contains(machine);
                    bool next = EditorGUILayout.ToggleLeft(names[i], selected);
                    if (next != selected)
                    {
                        if (next) _layerMergeSources.Add(machine);
                        else _layerMergeSources.Remove(machine);
                    }
                }
            }
            var sharedNames = GetLayerMergeSharedNames(layers);
            if (sharedNames.Count > 0)
            {
                int sharedIndex = Mathf.Max(0, sharedNames.IndexOf(_layerMergeSharedName));
                sharedIndex = HubPopup("LayerMergeShared_" + controller.GetInstanceID(), "Shared State", sharedIndex, sharedNames.ToArray());
                _layerMergeSharedName = sharedNames[sharedIndex];
            }
            else
            {
                _layerMergeSharedName = "";
                EditorGUILayout.HelpBox("Select source layers with a matching state name at the top of each state machine.", MessageType.Info);
            }
            EditorGUILayout.LabelField("The existing shared state keeps its motion and settings. If missing, it is copied from the first source. Shared-state behaviours and transitions are combined.", EditorStyles.wordWrappedMiniLabel);
            _layerMergeRemoveSources = EditorGUILayout.ToggleLeft(new GUIContent("Remove Source Layers",
                "Remove the selected source layers after copying. Off keeps the original layers. The merge supports Undo."), _layerMergeRemoveSources);
            return DrawAnimationToolActions("Check", "Merge Layers", _layerMergeTarget != null &&
                layers[targetIndex].syncedLayerIndex < 0 && sharedNames.Count > 0);
        }

        private List<string> GetLayerMergeSharedNames(AnimatorControllerLayer[] layers)
        {
            HashSet<string> common = null;
            foreach (var layer in layers)
            {
                var machine = layer.stateMachine;
                if (machine == null || machine == _layerMergeTarget || layer.syncedLayerIndex >= 0 || !_layerMergeSources.Contains(machine)) continue;
                var names = new HashSet<string>(StringComparer.Ordinal);
                var duplicates = new HashSet<string>(StringComparer.Ordinal);
                foreach (var child in machine.states)
                    if (child.state != null && !names.Add(child.state.name)) duplicates.Add(child.state.name);
                names.ExceptWith(duplicates);
                if (common == null) common = names;
                else common.IntersectWith(names);
            }
            var result = common == null ? new List<string>() : new List<string>(common);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static AnimatorState FindLayerMergeSharedState(AnimatorStateMachine machine, string name)
        {
            AnimatorState found = null;
            foreach (var child in machine.states)
            {
                if (child.state == null || child.state.name != name) continue;
                if (found != null) throw new InvalidOperationException("More than one state named '" + name + "' exists in '" + machine.name + "'. Give these states unique names first.");
                found = child.state;
            }
            return found;
        }

        private void RunAnimatorLayerMerge(bool apply)
        {
            const string title = "Layer Merger";
            var controller = GetAnimatorController();
            if (controller == null || controller != _layerMergerController) return;
            var layers = controller.layers;
            var sourceIndices = new List<int>();
            int targetIndex = Array.FindIndex(layers, layer => layer.stateMachine == _layerMergeTarget);
            AnimatorState targetShared;
            try
            {
                string path = AssetDatabase.GetAssetPath(controller);
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !AssetDatabase.IsOpenForEdit(path) ||
                    (System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) != 0)
                    throw new InvalidOperationException("Use an editable controller saved inside this project's Assets folder.");
                if (targetIndex < 0 || _layerMergeTarget == null || layers[targetIndex].syncedLayerIndex >= 0)
                    throw new InvalidOperationException("Choose a target layer that is not synced to another layer.");
                for (int i = 0; i < layers.Length; i++)
                {
                    if (i != targetIndex && layers[i].stateMachine == _layerMergeTarget)
                        throw new InvalidOperationException("The target shares its state machine with another layer. Separate it before merging.");
                    if (i != targetIndex && layers[i].stateMachine != null && _layerMergeSources.Contains(layers[i].stateMachine))
                    {
                        if (layers[i].syncedLayerIndex >= 0) throw new InvalidOperationException("Synced layers cannot be merged as sources.");
                        sourceIndices.Add(i);
                    }
                }
                if (sourceIndices.Count == 0 || !GetLayerMergeSharedNames(layers).Contains(_layerMergeSharedName))
                    throw new InvalidOperationException("Select source layers and a shared state first.");
                targetShared = FindLayerMergeSharedState(_layerMergeTarget, _layerMergeSharedName);
                var targetObjects = new List<UnityEngine.Object>();
                var targetOwned = new HashSet<UnityEngine.Object>();
                CollectLayerMergeObjects(_layerMergeTarget, targetObjects, targetOwned, null);
                var uniqueRoots = new HashSet<AnimatorStateMachine>();
                foreach (int index in sourceIndices)
                {
                    var source = layers[index].stateMachine;
                    if (!uniqueRoots.Add(source)) throw new InvalidOperationException("Selected layers share the same state machine. Select independent layers to merge.");
                    var graph = new List<UnityEngine.Object>();
                    CollectLayerMergeObjects(source, graph, new HashSet<UnityEngine.Object>(), null);
                    var owned = new HashSet<UnityEngine.Object>(graph);
                    foreach (var obj in graph)
                    {
                        if ((obj is AnimatorState || obj is AnimatorStateMachine) && targetOwned.Contains(obj))
                            throw new InvalidOperationException("A source shares state objects with the target. Choose independent state machines before merging.");
                        var transition = obj as AnimatorTransitionBase;
                        if (transition != null && ((transition.destinationState != null && !owned.Contains(transition.destinationState)) ||
                            (transition.destinationStateMachine != null && !owned.Contains(transition.destinationStateMachine))))
                            throw new InvalidOperationException("Layer '" + layers[index].name + "' has a transition outside its state machine. Repair that transition before merging.");
                    }
                }
                if (_layerMergeRemoveSources) ValidateLayerMergeRemoval(layers, sourceIndices);
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog(title, exception.Message, "OK");
                return;
            }
            if (!apply)
            {
                EditorUtility.DisplayDialog(title, sourceIndices.Count + " source layer(s) can merge into '" + layers[targetIndex].name +
                    "' using shared state '" + _layerMergeSharedName + "'.\n\n" +
                    (targetShared == null ? "The shared state will be copied from the first source." : "The existing shared state's motion and settings will be kept.") +
                    "\nSource layers will be " + (_layerMergeRemoveSources ? "removed." : "kept.") +
                    "\nNested state machines, transitions, behaviours and Blend Trees will be copied. Animation clips remain shared assets.", "OK");
                return;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Merge Animator Layers");
            var created = new List<UnityEngine.Object>();
            try
            {
                Undo.RegisterCompleteObjectUndo(controller, "Merge Animator Layers");
                Undo.RegisterCompleteObjectUndo(_layerMergeTarget, "Merge Animator Layers");
                if (targetShared != null) Undo.RegisterCompleteObjectUndo(targetShared, "Merge Animator Layers");
                for (int i = 0; i < sourceIndices.Count; i++)
                    targetShared = CopyLayerMergeSource(controller, layers[sourceIndices[i]].stateMachine, _layerMergeTarget,
                        targetShared, _layerMergeSharedName, created);
                if (_layerMergeRemoveSources)
                {
                    var kept = new List<AnimatorControllerLayer>();
                    var indexMap = new int[layers.Length];
                    for (int i = 0; i < layers.Length; i++)
                    {
                        indexMap[i] = sourceIndices.Contains(i) ? -1 : kept.Count;
                        if (indexMap[i] >= 0) kept.Add(layers[i]);
                    }
                    foreach (var layer in kept)
                        if (layer.syncedLayerIndex >= 0) layer.syncedLayerIndex = indexMap[layer.syncedLayerIndex];
                    // Detach source layers without destroying assets that other motions may still reference.
                    controller.layers = kept.ToArray();
                }
                EditorUtility.SetDirty(_layerMergeTarget);
                EditorUtility.SetDirty(targetShared);
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(undoGroup);
                foreach (var obj in created) if (obj != null) DestroyImmediate(obj, true);
                EditorUtility.DisplayDialog(title, "The merge was rolled back.\n\n" + exception.Message, "OK");
                return;
            }
            _layerMergeSources.Clear();
            _animationToolClipsDirty = true;
            _hasParamCache = false;
            InvalidateLayerSummaries();
            SelectLayer(controller, Array.FindIndex(controller.layers, layer => layer.stateMachine == _layerMergeTarget));
            RefreshAvailableTrees();
            if (_hasSetupScan) _setupChecksStale = true;
            Repaint();
            EditorUtility.DisplayDialog(title, "Merged " + sourceIndices.Count + " layer(s) into '" + layers[targetIndex].name +
                "'. Source layers were " + (_layerMergeRemoveSources ? "removed." : "kept.") + " You can undo this merge.", "OK");
        }

        private static void ValidateLayerMergeRemoval(AnimatorControllerLayer[] layers, List<int> removed)
        {
            if (removed.Contains(0)) throw new InvalidOperationException("Keep the base layer, or choose it as the target, when removing source layers.");
            for (int i = 0; i < layers.Length; i++)
            {
                int sync = layers[i].syncedLayerIndex;
                if (sync >= layers.Length || (!removed.Contains(i) && sync >= 0 && removed.Contains(sync)))
                    throw new InvalidOperationException("Layer '" + layers[i].name + "' has a sync dependency that must be changed before removing the source layers.");
            }
            // Layer Control behaviours address layers by number. Do not silently retarget those controls.
            var graph = new List<UnityEngine.Object>();
            var visited = new HashSet<UnityEngine.Object>();
            foreach (var layer in layers)
                if (layer.stateMachine != null) CollectLayerMergeObjects(layer.stateMachine, graph, visited, null);
            foreach (var obj in graph)
            {
                if (!(obj is StateMachineBehaviour) || obj.GetType().Name != "VRCAnimatorLayerControl") continue;
                using (var serialized = new SerializedObject(obj))
                {
                    var layerProperty = serialized.FindProperty("layer");
                    if (layerProperty == null || layerProperty.propertyType != SerializedPropertyType.Integer) continue;
                    int referencedIndex = layerProperty.intValue;
                    if (removed.Exists(index => index <= referencedIndex))
                        throw new InvalidOperationException("An Animator Layer Control uses a layer number affected by removal. Keep the source layers, or resolve that Layer Control before removing them.");
                }
            }
        }

        private static void CollectLayerMergeObjects(UnityEngine.Object obj, List<UnityEngine.Object> objects,
            HashSet<UnityEngine.Object> visited, AnimatorState sharedStateKeepingMotion)
        {
            if (obj == null) throw new InvalidOperationException("A source state machine contains a missing state, transition or behaviour. Repair the missing reference before merging.");
            if (!visited.Add(obj)) return;
            objects.Add(obj);
            if (obj is AnimatorStateMachine machine)
            {
                foreach (var child in machine.states) CollectLayerMergeObjects(child.state, objects, visited, sharedStateKeepingMotion);
                foreach (var child in machine.stateMachines)
                {
                    CollectLayerMergeObjects(child.stateMachine, objects, visited, sharedStateKeepingMotion);
                    foreach (var transition in machine.GetStateMachineTransitions(child.stateMachine))
                        CollectLayerMergeObjects(transition, objects, visited, sharedStateKeepingMotion);
                }
                foreach (var transition in machine.anyStateTransitions) CollectLayerMergeObjects(transition, objects, visited, sharedStateKeepingMotion);
                foreach (var transition in machine.entryTransitions) CollectLayerMergeObjects(transition, objects, visited, sharedStateKeepingMotion);
                foreach (var behaviour in machine.behaviours) CollectLayerMergeObjects(behaviour, objects, visited, sharedStateKeepingMotion);
            }
            else if (obj is AnimatorState state)
            {
                foreach (var transition in state.transitions) CollectLayerMergeObjects(transition, objects, visited, sharedStateKeepingMotion);
                foreach (var behaviour in state.behaviours) CollectLayerMergeObjects(behaviour, objects, visited, sharedStateKeepingMotion);
                if (state != sharedStateKeepingMotion && state.motion is BlendTree tree)
                    CollectLayerMergeObjects(tree, objects, visited, sharedStateKeepingMotion);
            }
            else if (obj is BlendTree blendTree)
            {
                foreach (var child in blendTree.children)
                    if (child.motion is BlendTree nested) CollectLayerMergeObjects(nested, objects, visited, sharedStateKeepingMotion);
            }
        }

        private static AnimatorState CopyLayerMergeSource(AnimatorController controller, AnimatorStateMachine source,
            AnimatorStateMachine target, AnimatorState targetShared, string sharedName, List<UnityEngine.Object> created)
        {
            AnimatorState sourceShared = FindLayerMergeSharedState(source, sharedName);
            if (sourceShared == null) throw new InvalidOperationException("The shared source state changed. Select it again before merging.");
            bool reuseShared = targetShared != null;
            bool hadDefault = target.defaultState != null;
            var originals = new List<UnityEngine.Object>();
            CollectLayerMergeObjects(source, originals, new HashSet<UnityEngine.Object>(), reuseShared ? sourceShared : null);
            var copies = new Dictionary<UnityEngine.Object, UnityEngine.Object> { { source, target } };
            if (reuseShared) copies.Add(sourceShared, targetShared);
            foreach (var original in originals)
            {
                if (copies.ContainsKey(original)) continue;
                UnityEngine.Object copy = CreateLayerMergeObject(original);
                created.Add(copy);
                EditorUtility.CopySerialized(original, copy);
                copy.name = original.name;
                copy.hideFlags = HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(copy, controller);
                Undo.RegisterCreatedObjectUndo(copy, "Merge Animator Layers");
                copies.Add(original, copy);
            }
            // Remap all serialized references, including custom behaviour references and
            // state-machine transition tables, after every destination object exists.
            foreach (var original in originals)
            {
                if (original == source || (reuseShared && original == sourceShared)) continue;
                var copy = copies[original];
                using (var serialized = new SerializedObject(copy))
                {
                    var property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var reference = property.objectReferenceValue;
                        if (reference != null && copies.TryGetValue(reference, out var replacement))
                            property.objectReferenceValue = replacement;
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                EditorUtility.SetDirty(copy);
            }
            if (!reuseShared) targetShared = (AnimatorState)copies[sourceShared];
            foreach (var child in source.states)
            {
                if (reuseShared && child.state == sourceShared) continue;
                var copy = (AnimatorState)copies[child.state];
                copy.name = target.MakeUniqueStateName(child.state.name);
                target.AddState(copy, child.position);
            }
            foreach (var child in source.stateMachines)
            {
                var copy = (AnimatorStateMachine)copies[child.stateMachine];
                copy.name = target.MakeUniqueStateMachineName(child.stateMachine.name);
                target.AddStateMachine(copy, child.position);
                target.SetStateMachineTransitions(copy, AppendLayerMergeObjects(new AnimatorTransition[0],
                    source.GetStateMachineTransitions(child.stateMachine), copies));
            }
            if (reuseShared)
            {
                targetShared.behaviours = AppendLayerMergeObjects(targetShared.behaviours, sourceShared.behaviours, copies);
                targetShared.transitions = AppendLayerMergeObjects(targetShared.transitions, sourceShared.transitions, copies);
            }
            target.behaviours = AppendLayerMergeObjects(target.behaviours, source.behaviours, copies);
            target.anyStateTransitions = AppendLayerMergeObjects(target.anyStateTransitions, source.anyStateTransitions, copies);
            target.entryTransitions = AppendLayerMergeObjects(target.entryTransitions, source.entryTransitions, copies);
            if (!hadDefault)
                target.defaultState = source.defaultState != null && copies.TryGetValue(source.defaultState, out var mappedDefault)
                    ? (AnimatorState)mappedDefault : targetShared;
            return targetShared;
        }

        private static UnityEngine.Object CreateLayerMergeObject(UnityEngine.Object original)
        {
            if (original is AnimatorStateMachine) return new AnimatorStateMachine();
            if (original is AnimatorState) return new AnimatorState();
            if (original is AnimatorStateTransition) return new AnimatorStateTransition();
            if (original is AnimatorTransition) return new AnimatorTransition();
            if (original is BlendTree) return new BlendTree();
            if (original is StateMachineBehaviour) return ScriptableObject.CreateInstance(original.GetType());
            throw new InvalidOperationException("Cannot copy animator object of type " + original.GetType().Name + ".");
        }

        private static T[] AppendLayerMergeObjects<T>(T[] existing, T[] source,
            Dictionary<UnityEngine.Object, UnityEngine.Object> copies) where T : UnityEngine.Object
        {
            var result = new List<T>(existing);
            foreach (var original in source) result.Add((T)copies[original]);
            return result.ToArray();
        }

        private float GetAnimationToolClipListMinimumHeight()
        {
            bool hasClips = _animationToolExtraClips.Exists(clip => clip != null) ||
                (_showFxAnimationClips && GetAnimationToolClips().Count > 0);
            return hasClips ? AnimationToolClipListMinimumHeight : AnimationToolEmptyListMinimumHeight;
        }

        private float GetAnimationToolMinimumHeight(int index)
        {
            return index < _animationToolControlsHeights.Length
                ? _animationToolControlsHeights[index] + GetAnimationToolClipListMinimumHeight() : 80f;
        }

        private void DrawAnimationToolResizer(int index)
        {
            Event e = Event.current;
            int id = GUIUtility.GetControlID(73140 + index, FocusType.Passive);
            Rect rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(6), GUILayout.ExpandWidth(true));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);
            if (e.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(rect.x, rect.y + 2, rect.width, 2), new Color(0.15f, 0.15f, 0.15f));
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                _animationToolResizeIndex = index;
                _animationToolResizeControlId = id;
                GUIUtility.hotControl = id;
                e.Use();
            }
            if (_animationToolResizeIndex != index) return;
            if (e.type == EventType.MouseDrag)
            {
                float minimumHeight = GetAnimationToolMinimumHeight(index);
                float currentHeight = Mathf.Max(_animationToolHeights[index], minimumHeight);
                _animationToolHeights[index] = Mathf.Clamp(currentHeight + e.delta.y, minimumHeight, Mathf.Max(minimumHeight, 1200f));
                Repaint();
                e.Use();
            }
            if (e.rawType == EventType.MouseUp && e.button == 0)
            {
                FinishAnimationToolResize();
                e.Use();
            }
        }

        private void FinishAnimationToolResize()
        {
            if (_animationToolResizeIndex < 0) return;
            EditorPrefs.SetFloat(AnimationToolHeightKeys[_animationToolResizeIndex], _animationToolHeights[_animationToolResizeIndex]);
            if (GUIUtility.hotControl == _animationToolResizeControlId) GUIUtility.hotControl = 0;
            _animationToolResizeIndex = -1;
            _animationToolResizeControlId = 0;
        }

        private List<AnimationClip> GetAnimationToolClips()
        {
            if (!_animationToolClipsDirty && _animationToolSourceAsset == _targetAsset &&
                EditorApplication.timeSinceStartup < _nextAnimationToolClipRefresh) return _animationToolClips;
            // Keep the row count stable between Layout and Repaint, including when a clip is
            // dropped into one panel while the other panel shares the same selection.
            if (Event.current != null && Event.current.type != EventType.Layout) return _animationToolClips;
            _animationToolSourceAsset = _targetAsset;
            _animationToolClipsDirty = false;
            _nextAnimationToolClipRefresh = EditorApplication.timeSinceStartup + 2;
            var unique = new HashSet<AnimationClip>();
            if (_targetAsset is AnimatorOverrideController overrides)
            {
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                overrides.GetOverrides(pairs);
                foreach (var pair in pairs) unique.Add(pair.Value != null ? pair.Value : pair.Key);
            }
            else if (_targetAsset is RuntimeAnimatorController controller)
                foreach (var clip in controller.animationClips) unique.Add(clip);
            else if (_targetAsset is BlendTree tree)
                CollectAnimationToolTreeClips(tree, unique, new HashSet<BlendTree>());
            unique.Remove(null);
            _fxAnimationToolExcludedClips.RemoveWhere(clip => clip == null || !unique.Contains(clip));
            _animationToolClips.Clear();
            _animationToolClips.AddRange(unique);
            _animationToolClips.Sort((a, b) =>
            {
                int result = string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
                return result != 0 ? result : string.Compare(AssetDatabase.GetAssetPath(a), AssetDatabase.GetAssetPath(b), StringComparison.Ordinal);
            });
            return _animationToolClips;
        }

        private static void CollectAnimationToolTreeClips(BlendTree tree, HashSet<AnimationClip> clips, HashSet<BlendTree> visited)
        {
            if (tree == null || !visited.Add(tree)) return;
            foreach (var child in tree.children)
            {
                if (child.motion is AnimationClip clip) clips.Add(clip);
                else if (child.motion is BlendTree nested) CollectAnimationToolTreeClips(nested, clips, visited);
            }
        }

        private List<AnimationClip> GetSelectedAnimationToolClips()
        {
            var selected = new List<AnimationClip>();
            var unique = new HashSet<AnimationClip>();
            foreach (var clip in _animationToolExtraClips)
                if (clip != null && !_manualAnimationToolExcludedClips.Contains(clip) && unique.Add(clip)) selected.Add(clip);
            if (_showFxAnimationClips)
                foreach (var clip in GetAnimationToolClips())
                    if (clip != null && !_fxAnimationToolExcludedClips.Contains(clip) && unique.Add(clip)) selected.Add(clip);
            return selected;
        }

        private List<AnimationClip> GetAnimationMergerChoices()
        {
            // The base can be any available clip, including unchecked source clips.
            var available = new List<AnimationClip>(GetAnimationToolClips());
            var unique = new HashSet<AnimationClip>(available);
            foreach (var clip in _animationToolExtraClips)
                if (clip != null && unique.Add(clip)) available.Add(clip);
            return available;
        }

        private List<AnimationClip> GetSelectedAnimationMergerClips()
        {
            // Keep the displayed order: manual clips first, then FX clips. The shared
            // selection already deduplicates clips that appear in both lists.
            var sources = GetSelectedAnimationToolClips();
            sources.RemoveAll(clip => clip == _mergerBaseClip);
            return sources;
        }

        private void AddAnimationToolClip(AnimationClip clip)
        {
            if (clip == null) return;
            if (!_animationToolExtraClips.Contains(clip)) _animationToolExtraClips.Add(clip);
            _manualAnimationToolExcludedClips.Remove(clip);
            Repaint();
        }

        private void HandleAnimationToolClipDrop(Rect dropArea)
        {
            Event e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) ||
                !dropArea.Contains(e.mousePosition)) return;

            // Capture the entire selection before accepting the drag. Handle it before
            // the ObjectField, which otherwise accepts only one animation from the group.
            var droppedClips = new List<AnimationClip>();
            foreach (UnityEngine.Object obj in DragAndDrop.objectReferences)
                if (obj is AnimationClip candidate && candidate != null) droppedClips.Add(candidate);
            DragAndDrop.visualMode = droppedClips.Count > 0
                ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (droppedClips.Count > 0 && e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                foreach (AnimationClip clip in droppedClips) AddAnimationToolClip(clip);
            }
            e.Use();
        }

        private void DrawAnimationToolClipSelection(int toolIndex)
        {
            if (Event.current.type == EventType.Layout)
            {
                _animationToolExtraClips.RemoveAll(clip => clip == null);
                _manualAnimationToolExcludedClips.RemoveWhere(clip => clip == null || !_animationToolExtraClips.Contains(clip));
            }
            EditorGUI.BeginChangeCheck();
            _showFxAnimationClips = EditorGUILayout.ToggleLeft(new GUIContent("Show FX Animations",
                "Show animations from the controller slot on the right and include its checked clips when processing. Off processes only the manual list."), _showFxAnimationClips);
            if (EditorGUI.EndChangeCheck())
                EditorPrefs.SetBool(GetProjectScopedPreferenceKey(PrefShowFxAnimationClipsKey), _showFxAnimationClips);

            // Explicitly size both list viewports from the panel height. Nested layout
            // groups can otherwise stop stretching and leave unused space below them.
            // Animation Merger now uses the enclosing Merger panel's saved height.
            int heightIndex = toolIndex == 2 ? 3 : toolIndex;
            float listHeight = Mathf.Max(GetAnimationToolClipListMinimumHeight(),
                _animationToolHeights[heightIndex] - _animationToolControlsHeights[toolIndex]);
            Rect columnsRect = EditorGUILayout.BeginHorizontal();
            DrawAnimationToolClipColumn(toolIndex, false, listHeight);
            if (_showFxAnimationClips) DrawAnimationToolClipColumn(toolIndex, true, listHeight);
            EditorGUILayout.EndHorizontal();

            if (Event.current.type == EventType.Repaint)
            {
                // These coordinates are inside the panel's scroll view. Include its final
                // bottom margin so the columns finish immediately above the resize bar.
                float bottomMargin = Mathf.Max(GUI.skin.scrollView.padding.bottom, EditorStyles.helpBox.margin.bottom);
                float controlsHeight = Mathf.Ceil(columnsRect.yMax + bottomMargin - listHeight);
                if (controlsHeight > 0f && Mathf.Abs(_animationToolControlsHeights[toolIndex] - controlsHeight) > 0.5f)
                {
                    _animationToolControlsHeights[toolIndex] = controlsHeight;
                    Repaint();
                }
            }
        }

        private void DrawAnimationToolClipColumn(int toolIndex, bool fx, float listHeight)
        {
            const float dropAreaHeight = 80f;
            // A snapshot keeps rows stable during Add, Remove and Clear button events.
            AnimationClip[] clips = (fx ? GetAnimationToolClips() : _animationToolExtraClips).ToArray();
            var excluded = fx ? _fxAnimationToolExcludedClips : _manualAnimationToolExcludedClips;
            int selected = 0;
            foreach (var clip in clips)
                if (clip != null && !excluded.Contains(clip) && (toolIndex != 2 || clip != _mergerBaseClip)) selected++;
            Rect columnRect = EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            // Accept multiple clips anywhere in the manual column, including its picker and rows.
            if (!fx) HandleAnimationToolClipDrop(columnRect);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label((fx ? "FX Animations (" : "Manual Animations (") + clips.Length + ")", EditorStyles.boldLabel, GUILayout.MinWidth(0));
            GUILayout.FlexibleSpace();
            bool clear = false;
            if (fx)
            {
                if (GUILayout.Button("Refresh", GUILayout.Width(60))) _animationToolClipsDirty = true;
            }
            else clear = GUILayout.Button(new GUIContent("Clear All", "Remove every animation from the manual list. Animation assets are kept."), GUILayout.Width(66));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("All", GUILayout.Width(40))) excluded.Clear();
            if (GUILayout.Button("None", GUILayout.Width(48)))
            {
                excluded.Clear();
                foreach (var clip in clips) if (clip != null) excluded.Add(clip);
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(selected + " selected", EditorStyles.miniLabel, GUILayout.MinWidth(0));
            EditorGUILayout.EndHorizontal();

            if (fx)
            {
                Rect sourceRect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.helpBox, GUILayout.Height(dropAreaHeight), GUILayout.ExpandWidth(true));
                string source = _targetAsset != null ? "From controller: " + _targetAsset.name : "Assign an Animation Controller above.";
                GUI.Label(sourceRect, new GUIContent(source, source), EditorStyles.wordWrappedMiniLabel);
                // Align the list with the manual column's clip picker.
                GUILayout.Space(EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing);
            }
            else
            {
                Rect dropArea = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.helpBox, GUILayout.Height(dropAreaHeight), GUILayout.ExpandWidth(true));
                GUI.Box(dropArea, GUIContent.none, EditorStyles.helpBox);
                GUI.Label(dropArea, "Drop one or more animation clips here", EditorStyles.centeredGreyMiniLabel);
                AnimationClip extra = (AnimationClip)EditorGUILayout.ObjectField(GUIContent.none, null, typeof(AnimationClip), false, GUILayout.MinWidth(0));
                if (extra != null) AddAnimationToolClip(extra);
            }

            AnimationClip removeExtra = null;
            var positions = fx ? _fxAnimationToolScroll : _manualAnimationToolScroll;
            if (clips.Length == 0)
            {
                // Empty lists need room for their message, but no scrollbar at all.
                positions[toolIndex] = Vector2.zero;
                EditorGUILayout.BeginVertical(GUILayout.Height(listHeight));
                EditorGUILayout.LabelField(fx ? "No animations in the loaded controller." : "Drag in animations or use the clip picker above.", EditorStyles.wordWrappedMiniLabel);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndVertical();
            }
            // This viewport takes the space left above the resize bar. Its content can
            // scroll independently, with enough height for the scrollbar arrows and thumb.
            else using (var scroll = new NestedScrollViewScope(ref positions[toolIndex],
                GUILayout.Height(listHeight)))
            {
                foreach (var clip in clips)
                {
                    if (clip == null) continue;
                    EditorGUILayout.BeginHorizontal();
                    bool isMergeBase = toolIndex == 2 && clip == _mergerBaseClip;
                    bool included = !isMergeBase && !excluded.Contains(clip);
                    using (new EditorGUI.DisabledScope(isMergeBase))
                    {
                        bool include = EditorGUILayout.Toggle(included, GUILayout.Width(18));
                        if (include != included)
                        {
                            if (include) excluded.Remove(clip);
                            else excluded.Add(clip);
                        }
                    }
                    string clipLabel = isMergeBase ? clip.name + " (Base)" : clip.name;
                    string clipTooltip = AssetDatabase.GetAssetPath(clip) + (isMergeBase ? "\nThe base animation is skipped as a merge source." : "");
                    if (GUILayout.Button(new GUIContent(clipLabel, clipTooltip), EditorStyles.objectField, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true)))
                    {
                        Selection.activeObject = clip;
                        EditorGUIUtility.PingObject(clip);
                    }
                    if (!fx && GUILayout.Button(new GUIContent("X", "Remove this animation from the manual list."), GUILayout.Width(23))) removeExtra = clip;
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUILayout.EndVertical();
            if (clear)
            {
                _animationToolExtraClips.Clear();
                _manualAnimationToolExcludedClips.Clear();
                for (int i = 0; i < _manualAnimationToolScroll.Length; i++) _manualAnimationToolScroll[i] = Vector2.zero;
                Repaint();
            }
            else if (removeExtra != null)
            {
                _animationToolExtraClips.Remove(removeExtra);
                _manualAnimationToolExcludedClips.Remove(removeExtra);
                Repaint();
            }
        }

        private int DrawBlendshapeMapperControls()
        {
            EditorGUILayout.BeginHorizontal();
            _mapperRemoveOldPaths = EditorGUILayout.ToggleLeft(new GUIContent("Remove Old Paths", "Remove matched source paths after mapping to the target meshes. Unmatched curves are kept."), _mapperRemoveOldPaths);
            _mapperCreateNewFiles = EditorGUILayout.ToggleLeft(new GUIContent("Create New Files", "Save copies beside the source clips. Controller references continue using the originals."), _mapperCreateNewFiles);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Maps animated blendshape names to matching meshes on the target avatar, including inactive meshes.", EditorStyles.wordWrappedMiniLabel);
            int action = DrawAnimationToolActions("Check", "Process Blendshapes", _avatarRoot != null && GetSelectedAnimationToolClips().Count > 0);
            DrawAnimationToolClipSelection(0);
            return action;
        }

        private int DrawPathReplaceControls()
        {
            EditorGUILayout.BeginHorizontal();
            DrawAnimationPathInput("Search for", ref _pathSearch, ref _pathSearchObject, ref _pathSearchExcludeRoot);
            DrawAnimationPathInput("Replace with", ref _pathReplacement, ref _pathReplacementObject, ref _pathReplacementExcludeRoot);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            _pathExactMatch = EditorGUILayout.ToggleLeft(new GUIContent("Exact Match Only", "Otherwise replace the matching text within paths. An empty exact search matches the avatar root."), _pathExactMatch);
            _pathCreateNewFiles = EditorGUILayout.ToggleLeft(new GUIContent("Create New Files", "Save copies beside the source clips. Controller references continue using the originals."), _pathCreateNewFiles);
            EditorGUILayout.EndHorizontal();
            bool canRun = (_pathExactMatch || !string.IsNullOrEmpty(_pathSearch)) && _pathSearch != _pathReplacement && GetSelectedAnimationToolClips().Count > 0;
            int action = DrawAnimationToolActions("Check", "Replace Paths", canRun);
            DrawAnimationToolClipSelection(1);
            return action;
        }

        private void DrawAnimationPathInput(string label, ref string path, ref GameObject sourceObject, ref bool excludeRoot)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.MinWidth(0), GUILayout.ExpandWidth(true));
            GUILayout.Label(label, EditorStyles.miniLabel);
            path = EditorGUILayout.TextField(path).Replace('\\', '/');
            GUILayout.Label("Get path from object", EditorStyles.miniLabel);
            EditorGUI.BeginChangeCheck();
            sourceObject = (GameObject)EditorGUILayout.ObjectField(sourceObject, typeof(GameObject), true);
            excludeRoot = EditorGUILayout.ToggleLeft("Exclude Root", excludeRoot);
            bool update = EditorGUI.EndChangeCheck();
            if (update && sourceObject != null) path = GetAnimationToolObjectPath(sourceObject, excludeRoot);
            EditorGUILayout.EndVertical();
        }

        private string GetAnimationToolObjectPath(GameObject target, bool excludeRoot)
        {
            Transform root = _avatarRoot != null ? _avatarRoot.transform : null;
            if (root == null || (target.transform != root && !target.transform.IsChildOf(root)))
            {
                var avatar = target.GetComponentInParent<VRCAvatarDescriptor>(true);
                var animator = target.GetComponentInParent<Animator>(true);
                root = avatar != null ? avatar.transform : animator != null ? animator.transform : target.transform.root;
            }
            string relative = AnimationUtility.CalculateTransformPath(target.transform, root);
            return excludeRoot ? relative : root.name + (relative.Length > 0 ? "/" + relative : "");
        }

        private int DrawAnimationMergerControls()
        {
            DrawAnimationToolClipPicker("Base Animation", _mergerBaseClip, clip => _mergerBaseClip = clip);
            EditorGUILayout.HelpBox("Merge Animation clips into Base Animation.", MessageType.Info);
            _mergerCreateNewFile = EditorGUILayout.ToggleLeft(new GUIContent("Create New File", "Save a merged copy with everything in it instead of adding evrtyhing to your base animation."), _mergerCreateNewFile);
            int sourceCount = GetSelectedAnimationMergerClips().Count;
            int action = DrawAnimationToolActions("Check Overlaps", "Merge Animations (" + sourceCount + ")", _mergerBaseClip != null && sourceCount > 0);
            DrawAnimationToolClipSelection(2);
            return action;
        }

        private void DrawAnimationToolClipPicker(string label, AnimationClip value, Action<AnimationClip> assign)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            var clip = (AnimationClip)EditorGUILayout.ObjectField(label, value, typeof(AnimationClip), false);
            if (EditorGUI.EndChangeCheck()) assign(clip);
            var available = GetAnimationMergerChoices();
            using (new EditorGUI.DisabledScope(available.Count == 0))
            {
                if (HubDropdownButton(new GUIContent("Choose", "Choose from the loaded controller and manually added clips."), GUILayout.Width(65)))
                {
                    var menu = new HubDropdownMenu(this);
                    for (int i = 0; i < available.Count; i++)
                    {
                        AnimationClip option = available[i];
                        menu.AddItem(new GUIContent((i + 1) + ". " + option.name.Replace('/', '∕')), option == value, () => { assign(option); Repaint(); });
                    }
                    menu.ShowAsContext();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static int DrawAnimationToolActions(string checkLabel, string actionLabel, bool enabled)
        {
            int action = 0;
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!enabled))
            {
                if (GUILayout.Button(checkLabel)) action = 1;
                if (GUILayout.Button(actionLabel)) action = 2;
            }
            EditorGUILayout.EndHorizontal();
            return action;
        }

        private sealed class AnimationToolEditPlan
        {
            public AnimationClip clip;
            public int matches;
            public string problem;
            public readonly Dictionary<EditorCurveBinding, AnimationCurve> floats = new Dictionary<EditorCurveBinding, AnimationCurve>();
            public readonly Dictionary<EditorCurveBinding, ObjectReferenceKeyframe[]> objects = new Dictionary<EditorCurveBinding, ObjectReferenceKeyframe[]>();
            public readonly HashSet<EditorCurveBinding> removeFloats = new HashSet<EditorCurveBinding>();
            public readonly HashSet<EditorCurveBinding> removeObjects = new HashSet<EditorCurveBinding>();
            public bool HasChanges => floats.Count > 0 || objects.Count > 0 || removeFloats.Count > 0 || removeObjects.Count > 0;
        }

        private void RunBlendshapeMapper(bool apply)
        {
            GameObject root = _avatarRoot;
            if (root == null) return;
            var targets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = renderer.sharedMesh;
                if (mesh == null) continue;
                string path = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    string name = mesh.GetBlendShapeName(i);
                    if (!targets.TryGetValue(name, out var paths)) targets[name] = paths = new HashSet<string>(StringComparer.Ordinal);
                    paths.Add(path);
                }
            }
            var plans = new List<AnimationToolEditPlan>();
            foreach (var clip in GetSelectedAnimationToolClips())
            {
                var plan = new AnimationToolEditPlan { clip = clip };
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(SkinnedMeshRenderer) || !binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                    string name = binding.propertyName.Substring("blendShape.".Length);
                    if (!targets.TryGetValue(name, out var paths) || paths.Count == 0) continue;
                    AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve == null) continue;
                    plan.matches++;
                    foreach (string path in paths)
                    {
                        if (path == binding.path) continue;
                        var destination = binding;
                        destination.path = path;
                        AddAnimationToolFloatWrite(plan, destination, curve);
                    }
                    // Keep unmatched curves and bindings already targeting a matching mesh.
                    if (_mapperRemoveOldPaths && !paths.Contains(binding.path)) plan.removeFloats.Add(binding);
                }
                FinalizeAnimationToolPlan(plan);
                plans.Add(plan);
            }
            ProcessAnimationToolPlans("Blendshape Mapper", plans, _mapperCreateNewFiles, "_Mapped", apply);
        }

        private void RunPathReplacement(bool apply)
        {
            string search = (_pathSearch ?? "").Replace('\\', '/');
            string replacement = (_pathReplacement ?? "").Replace('\\', '/');
            if ((!_pathExactMatch && search.Length == 0) || search == replacement) return;
            var plans = new List<AnimationToolEditPlan>();
            foreach (var clip in GetSelectedAnimationToolClips())
            {
                var plan = new AnimationToolEditPlan { clip = clip };
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    bool match = _pathExactMatch ? binding.path == search : binding.path.Contains(search);
                    if (!match) continue;
                    AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                    if (curve == null) continue;
                    var destination = binding;
                    destination.path = _pathExactMatch ? replacement : binding.path.Replace(search, replacement);
                    plan.matches++;
                    plan.removeFloats.Add(binding);
                    AddAnimationToolFloatWrite(plan, destination, curve);
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    bool match = _pathExactMatch ? binding.path == search : binding.path.Contains(search);
                    if (!match) continue;
                    var keys = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                    if (keys == null) continue;
                    var destination = binding;
                    destination.path = _pathExactMatch ? replacement : binding.path.Replace(search, replacement);
                    plan.matches++;
                    plan.removeObjects.Add(binding);
                    if (plan.objects.TryGetValue(destination, out var existing) && !AnimationToolObjectCurvesEqual(existing, keys))
                        plan.problem = "Multiple different curves would use " + AnimationToolBindingName(destination) + ".";
                    else plan.objects[destination] = keys;
                }
                FinalizeAnimationToolPlan(plan);
                plans.Add(plan);
            }
            ProcessAnimationToolPlans("Animation Path Replacer", plans, _pathCreateNewFiles, "_Replaced", apply);
        }

        private void RunAnimationMerge(bool apply)
        {
            if (_mergerBaseClip == null) return;
            var sources = GetSelectedAnimationMergerClips();
            if (sources.Count == 0) return;
            var plan = new AnimationToolEditPlan { clip = _mergerBaseClip };
            var baseFloats = new HashSet<EditorCurveBinding>(AnimationUtility.GetCurveBindings(_mergerBaseClip));
            var baseObjects = new HashSet<EditorCurveBinding>(AnimationUtility.GetObjectReferenceCurveBindings(_mergerBaseClip));
            int sourceOverrides = 0;
            foreach (var source in sources)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(source))
                {
                    var curve = AnimationUtility.GetEditorCurve(source, binding);
                    if (curve == null) continue;
                    if (plan.floats.ContainsKey(binding)) sourceOverrides++;
                    plan.floats[binding] = curve;
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(source))
                {
                    var curve = AnimationUtility.GetObjectReferenceCurve(source, binding);
                    if (curve == null) continue;
                    if (plan.objects.ContainsKey(binding)) sourceOverrides++;
                    plan.objects[binding] = curve;
                }
            }
            int overlaps = 0;
            foreach (var binding in plan.floats.Keys) if (baseFloats.Contains(binding)) overlaps++;
            foreach (var binding in plan.objects.Keys) if (baseObjects.Contains(binding)) overlaps++;
            plan.matches = plan.floats.Count + plan.objects.Count;
            if (!apply)
            {
                var summary = new System.Text.StringBuilder("Base: " + _mergerBaseClip.name + "\nSource clips: " + sources.Count + "\n\nMerge order:\n");
                for (int i = 0; i < Mathf.Min(sources.Count, 12); i++) summary.AppendLine((i + 1) + ". " + sources[i].name);
                if (sources.Count > 12) summary.AppendLine("...and " + (sources.Count - 12) + " more in the displayed list order.");
                summary.Append("\n" + plan.matches + " distinct curves to merge: " + overlaps + " base properties will be overridden, " +
                    (plan.matches - overlaps) + " new properties will be added.\n" + sourceOverrides + " curve overrides between source clips.");
                summary.Append("\n\nManual clips are merged first, then FX clips, top to bottom. Later clips override matching properties. Duplicate clips and the base are skipped. Other base curves, clip settings and animation events are kept.");
                EditorUtility.DisplayDialog("Animation Merger — Check Overlaps",
                    summary.ToString(), "OK");
                return;
            }
            // Overwriting matching properties is intentional for the merger.
            ProcessAnimationToolPlans("Animation Merger", new List<AnimationToolEditPlan> { plan }, _mergerCreateNewFile, "_Merged", true);
        }

        private static string AnimationToolBindingName(EditorCurveBinding binding)
        {
            return "'" + (string.IsNullOrEmpty(binding.path) ? "<Avatar Root>" : binding.path) + " / " + binding.propertyName + "'";
        }

        private static void AddAnimationToolFloatWrite(AnimationToolEditPlan plan, EditorCurveBinding destination, AnimationCurve curve)
        {
            if (plan.floats.TryGetValue(destination, out var existing) && !AnimationToolCurvesEqual(existing, curve))
                plan.problem = "Multiple different curves would use " + AnimationToolBindingName(destination) + ".";
            else plan.floats[destination] = curve;
        }

        private static void FinalizeAnimationToolPlan(AnimationToolEditPlan plan)
        {
            var unchangedFloats = new List<EditorCurveBinding>();
            foreach (var pair in plan.floats)
            {
                if (plan.removeFloats.Contains(pair.Key)) continue;
                var existing = AnimationUtility.GetEditorCurve(plan.clip, pair.Key);
                if (existing == null) continue;
                if (AnimationToolCurvesEqual(existing, pair.Value)) unchangedFloats.Add(pair.Key);
                else plan.problem = "A different curve already uses " + AnimationToolBindingName(pair.Key) + ".";
            }
            foreach (var binding in unchangedFloats) plan.floats.Remove(binding);
            var unchangedObjects = new List<EditorCurveBinding>();
            foreach (var pair in plan.objects)
            {
                if (plan.removeObjects.Contains(pair.Key)) continue;
                var existing = AnimationUtility.GetObjectReferenceCurve(plan.clip, pair.Key);
                if (existing == null || existing.Length == 0) continue;
                if (AnimationToolObjectCurvesEqual(existing, pair.Value)) unchangedObjects.Add(pair.Key);
                else plan.problem = "A different curve already uses " + AnimationToolBindingName(pair.Key) + ".";
            }
            foreach (var binding in unchangedObjects) plan.objects.Remove(binding);
        }

        private static bool AnimationToolCurvesEqual(AnimationCurve a, AnimationCurve b)
        {
            if (a == null || b == null) return a == b;
            if (a.length != b.length || a.preWrapMode != b.preWrapMode || a.postWrapMode != b.postWrapMode) return false;
            var aKeys = a.keys;
            var bKeys = b.keys;
            for (int i = 0; i < aKeys.Length; i++)
                if (!aKeys[i].Equals(bKeys[i]) || AnimationUtility.GetKeyBroken(a, i) != AnimationUtility.GetKeyBroken(b, i) ||
                    AnimationUtility.GetKeyLeftTangentMode(a, i) != AnimationUtility.GetKeyLeftTangentMode(b, i) ||
                    AnimationUtility.GetKeyRightTangentMode(a, i) != AnimationUtility.GetKeyRightTangentMode(b, i)) return false;
            return true;
        }

        private static bool AnimationToolObjectCurvesEqual(ObjectReferenceKeyframe[] a, ObjectReferenceKeyframe[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].time != b[i].time || a[i].value != b[i].value) return false;
            return true;
        }

        private static AnimationCurve CopyAnimationToolCurve(AnimationCurve source)
        {
            if (source == null) return null;
            // Copy native curve data, including weighted tangents and editor tangent modes,
            // without recalculating the tangents through the mode setters.
            var result = new AnimationCurve();
            result.CopyFrom(source);
            return result;
        }

        private static string AnimationToolEditBlocker(AnimationClip clip)
        {
            string path = AssetDatabase.GetAssetPath(clip);
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) || AssetDatabase.IsSubAsset(clip))
                return "Choose Create New Files to edit this imported, packaged or unsaved clip.";
            if ((clip.hideFlags & HideFlags.NotEditable) != 0 || !AssetDatabase.IsOpenForEdit(path))
                return "The clip is read-only. Make it editable or choose Create New Files.";
            if (System.IO.File.Exists(path) && (System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReadOnly) != 0)
                return "The animation file is read-only. Make it editable or choose Create New Files.";
            return null;
        }

        private void ProcessAnimationToolPlans(string title, List<AnimationToolEditPlan> plans, bool createCopies, string suffix, bool apply)
        {
            var ready = new List<AnimationToolEditPlan>();
            var issues = new List<string>();
            int matches = 0;
            foreach (var plan in plans)
            {
                matches += plan.matches;
                string problem = plan.problem;
                if (problem == null && plan.HasChanges && !createCopies) problem = AnimationToolEditBlocker(plan.clip);
                if (problem != null) issues.Add(plan.clip.name + ": " + problem);
                else if (plan.HasChanges) ready.Add(plan);
            }
            if (!apply)
            {
                EditorUtility.DisplayDialog(title + " — Check",
                    matches + " matching curves in " + plans.Count + " selected clips.\n" + ready.Count + " clips would " +
                    (createCopies ? "be copied and updated." : "be updated.") + FormatAnimationToolIssues(issues), "OK");
                return;
            }
            if (ready.Count == 0)
            {
                EditorUtility.DisplayDialog(title, "No changes to apply." + FormatAnimationToolIssues(issues), "OK");
                return;
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(title);
            var outputs = new List<UnityEngine.Object>();
            try
            {
                foreach (var plan in ready)
                {
                    try { outputs.Add(ApplyAnimationToolPlan(plan, createCopies, suffix, title)); }
                    catch (Exception exception) { issues.Add(plan.clip.name + ": " + exception.Message); }
                }
            }
            finally { Undo.CollapseUndoOperations(undoGroup); }
            if (outputs.Count > 0)
            {
                AssetDatabase.SaveAssets();
                _animationToolClipsDirty = true;
                if (_hasSetupScan) _setupChecksStale = true;
                if (createCopies)
                {
                    Selection.objects = outputs.ToArray();
                    EditorGUIUtility.PingObject(outputs[0]);
                }
            }
            EditorUtility.DisplayDialog(title,
                (createCopies ? "Created " : "Updated ") + outputs.Count + " animation clip(s)." +
                (createCopies && outputs.Count > 0 ? "\nNew clips are selected in the Project window. Controller references still use the original clips." : "") +
                FormatAnimationToolIssues(issues), "OK");
            Repaint();
        }

        private static string FormatAnimationToolIssues(List<string> issues)
        {
            if (issues.Count == 0) return "";
            var text = new System.Text.StringBuilder("\n\nSkipped clips (" + issues.Count + "):\n");
            for (int i = 0; i < Mathf.Min(issues.Count, 12); i++) text.AppendLine("• " + issues[i]);
            if (issues.Count > 12) text.AppendLine("…and " + (issues.Count - 12) + " more.");
            return text.ToString();
        }

        private AnimationClip ApplyAnimationToolPlan(AnimationToolEditPlan plan, bool createCopy, string suffix, string undoName)
        {
            AnimationClip target = plan.clip;
            AnimationClip backup = null;
            string createdPath = null;
            try
            {
                if (createCopy)
                {
                    string folder = System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(plan.clip));
                    folder = (folder ?? "").Replace('\\', '/');
                    if ((folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal)) || !AssetDatabase.IsValidFolder(folder))
                    {
                        folder = (_converterSaveFolderPath ?? "Assets").Replace('\\', '/').TrimEnd('/');
                        if ((folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal)) || !AssetDatabase.IsValidFolder(folder)) folder = "Assets";
                    }
                    string name = plan.clip.name;
                    foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                    name = name.Replace('/', '_').Replace('\\', '_').Trim();
                    if (name.Length == 0) name = "Animation";
                    createdPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + suffix + ".anim");
                    target = Instantiate(plan.clip);
                    target.name = name + suffix;
                    target.hideFlags = HideFlags.None;
                    AssetDatabase.CreateAsset(target, createdPath);
                    if (AssetDatabase.GetAssetPath(target) != createdPath)
                        throw new InvalidOperationException("Could not create the animation copy at " + createdPath + ".");
                    Undo.RegisterCreatedObjectUndo(target, undoName);
                }
                else
                {
                    backup = Instantiate(plan.clip);
                    backup.name = plan.clip.name;
                    Undo.RegisterCompleteObjectUndo(target, undoName);
                }
                foreach (var binding in plan.removeFloats) AnimationUtility.SetEditorCurve(target, binding, null);
                foreach (var binding in plan.removeObjects) AnimationUtility.SetObjectReferenceCurve(target, binding, null);
                foreach (var pair in plan.floats) AnimationUtility.SetEditorCurve(target, pair.Key, CopyAnimationToolCurve(pair.Value));
                foreach (var pair in plan.objects) AnimationUtility.SetObjectReferenceCurve(target, pair.Key, pair.Value);
                EditorUtility.SetDirty(target);
                return target;
            }
            catch
            {
                if (createdPath != null)
                {
                    if (AssetDatabase.GetAssetPath(target) == createdPath) AssetDatabase.DeleteAsset(createdPath);
                    else if (target != null && target != plan.clip) DestroyImmediate(target);
                }
                else if (backup != null)
                {
                    EditorUtility.CopySerialized(backup, plan.clip);
                    EditorUtility.SetDirty(plan.clip);
                }
                throw;
            }
            finally { if (backup != null) DestroyImmediate(backup); }
        }

        private void DrawAnimationConverter()
        {
            BlendTree tree = CurrentTree;
            if (tree == null) return;

            GUILayout.Space(10);
            _showConverter = EditorGUILayout.Foldout(_showConverter, "BlendTree Animation Converter", true, EditorStyles.foldoutHeader);
            if (_showConverter)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.HelpBox("Converts keyframes from a single Animation to thresholds in a blendtree and generates animations for each keyframe.", MessageType.Info);

                _converterSourceClip = (AnimationClip)EditorGUILayout.ObjectField("Source Animation", _converterSourceClip, typeof(AnimationClip), false);

                _converterParameter = DrawParameterComboBoxWithLabel("ConvParam", "Parameter", _converterParameter);

                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                _converterToggleMode = EditorGUILayout.ToggleLeft(new GUIContent("Snap Mode",
                    "Makes it like a toggle instead of a smooth slider."), _converterToggleMode, GUILayout.Width(100));
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetBool(PrefConverterToggleModeKey, _converterToggleMode);
                }
                EditorGUI.BeginChangeCheck();
                _converterSaveAutoFolderPath = EditorGUILayout.ToggleLeft(new GUIContent("Auto Folder Path",
                    "Uses animation path for generated animations."), _converterSaveAutoFolderPath, GUILayout.Width(145));
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetBool(GetProjectScopedPreferenceKey(PrefConverterAutoFolderKey), _converterSaveAutoFolderPath);
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();

                if (!_converterSaveAutoFolderPath)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUI.BeginChangeCheck();
                    string editedFolder = EditorGUILayout.TextField("Save Folder", _converterSaveFolderPath);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _converterSaveFolderPath = editedFolder;
                        EditorPrefs.SetString(GetProjectScopedPreferenceKey(PrefConverterFolderKey), _converterSaveFolderPath);
                    }

                    if (GUILayout.Button(new GUIContent("Use Open Folder",
                        "Use the folder open in the last-used Project window. In one-column view, select a folder or an asset inside it first."), GUILayout.Width(115)))
                        UseOpenProjectFolderForConverter();
                    EditorGUILayout.EndHorizontal();
                }

                if (_converterSourceClip != null)
                {
                    if (GUILayout.Button("Convert & Add as Sub-BlendTree", GUILayout.Height(30)))
                    {
                        ExecuteAnimationConversion();
                    }
                }
                EditorGUILayout.EndVertical();
            }
        }

        private void UseOpenProjectFolderForConverter()
        {
            string folder;
            try
            {
                // Unity 2022 exposes the active Project folder through an internal editor method.
                var getFolder = typeof(ProjectWindowUtil).GetMethod("TryGetActiveFolderPath",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                    null, new[] { typeof(string).MakeByRefType() }, null);
                object[] arguments = { null };
                if (getFolder != null && (bool)getFolder.Invoke(null, arguments))
                    folder = arguments[0] as string;
                else
                {
                    // One-column view has no separate open folder; use its selected asset/folder.
                    string selectedPath = AssetDatabase.GetAssetPath(Selection.activeObject);
                    folder = AssetDatabase.IsValidFolder(selectedPath) ? selectedPath
                        : string.IsNullOrEmpty(selectedPath) ? "" : System.IO.Path.GetDirectoryName(selectedPath);
                }
            }
            catch (Exception)
            {
                ShowNotification(new GUIContent("Could not read the Project folder. Enter the path in Save Folder."));
                return;
            }

            folder = (folder ?? "").Replace('\\', '/').TrimEnd('/');
            if ((folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal)) || !AssetDatabase.IsValidFolder(folder))
            {
                ShowNotification(new GUIContent("Open or select a folder inside Assets in the Project window first."));
                return;
            }
            GUI.FocusControl(null);
            _converterSaveFolderPath = folder;
            EditorPrefs.SetString(GetProjectScopedPreferenceKey(PrefConverterFolderKey), folder);
            Repaint();
        }

        private string GetConverterSourceFolder()
        {
            string sourcePath = AssetDatabase.GetAssetPath(_converterSourceClip);
            return string.IsNullOrEmpty(sourcePath)
                ? "" : (System.IO.Path.GetDirectoryName(sourcePath) ?? "").Replace('\\', '/');
        }

        private void ExecuteAnimationConversion()
        {
            BlendTree tree = CurrentTree;
            if (_converterSourceClip == null || tree == null) return;

            string saveFolder;
            if (_converterSaveAutoFolderPath)
            {
                saveFolder = GetConverterSourceFolder();
                if ((saveFolder != "Assets" && !saveFolder.StartsWith("Assets/", StringComparison.Ordinal)) ||
                    !AssetDatabase.IsValidFolder(saveFolder))
                {
                    EditorUtility.DisplayDialog("Auto Save Folder",
                        "The source animation must be saved inside this project's Assets folder to save beside it. Turn off Auto Save Folder to choose an output folder manually.", "OK");
                    return;
                }
            }
            else
            {
                if (string.IsNullOrEmpty(_converterSaveFolderPath)) _converterSaveFolderPath = "Assets";
                saveFolder = _converterSaveFolderPath.Replace('\\', '/').TrimEnd('/');
            }
            string generatedFolderPath = saveFolder + "/Generated";

            string absoluteSavePath = Application.dataPath.Substring(0, Application.dataPath.Length - 6) + saveFolder;
            if (!System.IO.Directory.Exists(absoluteSavePath))
            {
                System.IO.Directory.CreateDirectory(absoluteSavePath);
                AssetDatabase.Refresh();
            }

            string absoluteGenPath = Application.dataPath.Substring(0, Application.dataPath.Length - 6) + generatedFolderPath;
            if (!System.IO.Directory.Exists(absoluteGenPath))
            {
                System.IO.Directory.CreateDirectory(absoluteGenPath);
                AssetDatabase.Refresh();
            }

            EditorCurveBinding[] floatBindings = AnimationUtility.GetCurveBindings(_converterSourceClip);
            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(_converterSourceClip);
            HashSet<float> uniqueTimes = new HashSet<float>();

            foreach (var binding in floatBindings)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(_converterSourceClip, binding);
                if (curve != null)
                {
                    foreach (var key in curve.keys) uniqueTimes.Add(Mathf.Round(key.time * 10000f) / 10000f);
                }
            }
            foreach (var binding in objectBindings)
            {
                var keys = AnimationUtility.GetObjectReferenceCurve(_converterSourceClip, binding);
                if (keys != null)
                {
                    foreach (var key in keys) uniqueTimes.Add(Mathf.Round(key.time * 10000f) / 10000f);
                }
            }

            List<float> times = new List<float>(uniqueTimes);
            times.Sort();

            if (times.Count == 0)
            {
                EditorUtility.DisplayDialog("No Keyframes", "The source animation has no keyframes to convert.", "OK");
                return;
            }

            string baseName = _converterSourceClip.name + " KeyFrame-";
            Undo.RecordObject(tree, "Convert Animation to BlendTree");

            AnimatorController controller = GetAnimatorController();

            if (controller != null)
            {
                EnsureFloatParameterExists(controller, _converterParameter, 0f);
            }

            string targetSubTreeName = _converterSourceClip.name + " Generated";

            List<ChildMotion> currentTreeChildren = new List<ChildMotion>(tree.children);

            int insertIndex = -1;
            BlendTree treeToDestroy = null;

            for (int i = 0; i < currentTreeChildren.Count; i++)
            {
                if (currentTreeChildren[i].motion != null && currentTreeChildren[i].motion.name == targetSubTreeName)
                {
                    insertIndex = i;
                    treeToDestroy = currentTreeChildren[i].motion as BlendTree;
                    break;
                }
            }

            currentTreeChildren.RemoveAll(c => c.motion != null && c.motion.name == targetSubTreeName);

            currentTreeChildren.RemoveAll(c => c.motion == null);

            tree.children = currentTreeChildren.ToArray();

            if (treeToDestroy != null)
            {
                Undo.DestroyObjectImmediate(treeToDestroy);
            }

            BlendTree newSubTree = new BlendTree
            {
                name = targetSubTreeName,
                blendType = BlendTreeType.Simple1D,
                hideFlags = HideFlags.HideInHierarchy,
                useAutomaticThresholds = false,
                blendParameter = _converterParameter
            };

            string controllerAssetPath = AssetDatabase.GetAssetPath(tree);
            if (!string.IsNullOrEmpty(controllerAssetPath))
            {
                AssetDatabase.AddObjectToAsset(newSubTree, controllerAssetPath);
                AssetDatabase.SaveAssets();
            }

            List<ChildMotion> subTreeChildren = new List<ChildMotion>();
            float minTime = times[0];
            float maxTime = times[times.Count - 1];
            float timeRange = maxTime - minTime;
            AnimatorControllerParameterType converterParameterType = AnimatorControllerParameterType.Float;
            if (controller != null)
            {
                foreach (var parameter in controller.parameters)
                {
                    if (parameter.name == _converterParameter)
                    {
                        converterParameterType = parameter.type;
                        break;
                    }
                }
            }

            for (int i = 0; i < times.Count; i++)
            {
                float t = times[i];
                AnimationClip poseClip = new AnimationClip { name = baseName + i };

                foreach (var binding in floatBindings)
                {
                    AnimationCurve sourceCurve = AnimationUtility.GetEditorCurve(_converterSourceClip, binding);
                    if (sourceCurve != null)
                    {
                        float val = sourceCurve.Evaluate(t);

                        foreach (var key in sourceCurve.keys)
                        {
                            if (Mathf.Abs(key.time - t) <= 0.0002f)
                            {
                                val = key.value;
                                break;
                            }
                        }

                        AnimationCurve singleFrameCurve = new AnimationCurve(new Keyframe(0f, val));
                        AnimationUtility.SetEditorCurve(poseClip, binding, singleFrameCurve);
                    }
                }

                foreach (var binding in objectBindings)
                {
                    var keys = AnimationUtility.GetObjectReferenceCurve(_converterSourceClip, binding);
                    if (keys != null && keys.Length > 0)
                    {
                        UnityEngine.Object activeObj = keys[0].value;
                        foreach (var k in keys)
                        {
                            if (k.time <= t + 0.0002f) activeObj = k.value;
                            else break;
                        }
                        var singleFrameObjCurve = new ObjectReferenceKeyframe[1];
                        singleFrameObjCurve[0] = new ObjectReferenceKeyframe { time = 0f, value = activeObj };
                        AnimationUtility.SetObjectReferenceCurve(poseClip, binding, singleFrameObjCurve);
                    }
                }

                string assetPath = generatedFolderPath + "/" + poseClip.name + ".anim";
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath) != null)
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }
                AssetDatabase.CreateAsset(poseClip, assetPath);

                float normalizedThreshold = 0f;
                if (timeRange > 0f)
                {
                    normalizedThreshold = (t - minTime) / timeRange;
                }

                float finalThreshold = normalizedThreshold;
                if (converterParameterType == AnimatorControllerParameterType.Int && !_converterToggleMode)
                {
                    finalThreshold = Mathf.Round(normalizedThreshold);
                }

                subTreeChildren.Add(new ChildMotion
                {
                    motion = poseClip,
                    timeScale = 1f,
                    threshold = finalThreshold,
                    directBlendParameter = ""
                });
            }

            if (_converterToggleMode && subTreeChildren.Count > 1)
            {
                List<ChildMotion> sampledPoseChildren = subTreeChildren;
                List<ChildMotion> heldPoseChildren = new List<ChildMotion>(sampledPoseChildren.Count * 2 - 1);

                ChildMotion firstPose = sampledPoseChildren[0];
                firstPose.threshold = 0f;
                heldPoseChildren.Add(firstPose);

                for (int i = 1; i < sampledPoseChildren.Count; i++)
                {
                    float keyframeThreshold = sampledPoseChildren[i].threshold;

                    ChildMotion previousPose = sampledPoseChildren[i - 1];
                    previousPose.threshold = Mathf.Max(previousPose.threshold, keyframeThreshold - 0.001f);
                    heldPoseChildren.Add(previousPose);

                    ChildMotion currentPose = sampledPoseChildren[i];
                    currentPose.threshold = keyframeThreshold;
                    heldPoseChildren.Add(currentPose);
                }
                subTreeChildren = heldPoseChildren;
            }

            newSubTree.children = subTreeChildren.ToArray();
            EditorUtility.SetDirty(newSubTree);

            currentTreeChildren = new List<ChildMotion>(tree.children);

            string bestWeightParam = "";
            if (tree.blendType == BlendTreeType.Direct && currentTreeChildren.Count > 0)
            {
                Dictionary<string, int> paramCounts = new Dictionary<string, int>();
                foreach (var child in currentTreeChildren)
                {
                    string p = child.directBlendParameter;
                    if (!string.IsNullOrEmpty(p))
                    {
                        if (paramCounts.ContainsKey(p)) paramCounts[p]++;
                        else paramCounts[p] = 1;
                    }
                }

                int maxCount = 0;
                foreach (var kvp in paramCounts)
                {
                    if (kvp.Value > maxCount)
                    {
                        maxCount = kvp.Value;
                        bestWeightParam = kvp.Key;
                    }
                }
            }

            if (string.IsNullOrEmpty(bestWeightParam))
            {
                bestWeightParam = GetOrCreateFirstFloatParameter();
            }

            ChildMotion newMotionData = new ChildMotion
            {
                motion = newSubTree,
                timeScale = 1f,
                threshold = 0f,
                directBlendParameter = tree.blendType == BlendTreeType.Direct ? bestWeightParam : ""
            };

            if (insertIndex >= 0 && insertIndex <= currentTreeChildren.Count)
            {
                currentTreeChildren.Insert(insertIndex, newMotionData);
            }
            else
            {
                currentTreeChildren.Add(newMotionData);
            }

            tree.children = currentTreeChildren.ToArray();
            AssetDatabase.SaveAssets();

            _converterSourceClip = null;
            string generatedPoseSummary = _converterToggleMode
                ? $"{times.Count} generated poses across {subTreeChildren.Count} held-pose entries"
                : $"{times.Count} generated animations";
            EditorUtility.DisplayDialog("Success", $"Created a new 1D Blend Tree '{newSubTree.name}' with {generatedPoseSummary} and added it to '{tree.name}'.\n\nThe clips were saved in:\n{generatedFolderPath}", "OK");
        }

        private AnimatorController GetAnimatorController()
        {
            if (_targetAsset is AnimatorController c) return c;
            if (_targetAsset != null)
            {
                string path = AssetDatabase.GetAssetPath(_targetAsset);
                if (!string.IsNullOrEmpty(path))
                {
                    return AssetDatabase.LoadMainAssetAtPath(path) as AnimatorController;
                }
            }
            return null;
        }

        private void EnsureBlendTreeWeightFloatParameter(AnimatorController controller)
        {
            if (controller == null) return;

            AnimatorControllerParameter[] parameters = controller.parameters;
            int parameterIndex = Array.FindIndex(parameters, p => p.name == "BlendTreeWeight");
            if (parameterIndex < 0) return;

            AnimatorControllerParameterType oldType = parameters[parameterIndex].type;
            if (oldType != AnimatorControllerParameterType.Float)
            {
                Undo.RecordObject(controller, "Set BlendTreeWeight to Float");
                ConvertParameterType(controller, "BlendTreeWeight", oldType, AnimatorControllerParameterType.Float);
                parameters = controller.parameters;
                parameterIndex = Array.FindIndex(parameters, p => p.name == "BlendTreeWeight");
                if (parameterIndex < 0) return;
            }

            bool hasDuplicate = false;
            for (int i = parameterIndex + 1; i < parameters.Length; i++)
            {
                if (parameters[i].name == "BlendTreeWeight")
                {
                    hasDuplicate = true;
                    break;
                }
            }

            bool defaultNeedsUpdate = parameters[parameterIndex].defaultFloat != 1f;
            if (!hasDuplicate && !defaultNeedsUpdate) return;

            Undo.RecordObject(controller, "Normalize BlendTreeWeight Parameter");
            var normalizedParameters = new List<AnimatorControllerParameter>();
            bool keptBlendTreeWeight = false;
            foreach (var parameter in parameters)
            {
                if (parameter.name == "BlendTreeWeight")
                {
                    if (keptBlendTreeWeight) continue;
                    parameter.type = AnimatorControllerParameterType.Float;
                    parameter.defaultFloat = 1f;
                    keptBlendTreeWeight = true;
                }

                normalizedParameters.Add(parameter);
            }

            controller.parameters = normalizedParameters.ToArray();
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
        }

        private void EnsureFloatParameterExists(AnimatorController controller, string paramName, float defaultValue)
        {
            if (string.IsNullOrEmpty(paramName)) return;

            if (_hideVrcBuiltInParameters && paramName == "BlendTreeWeight")
            {
                RemoveAnimatorOnlyParametersFromVrc();
                EnsureBlendTreeWeightFloatParameter(controller);
            }

            if (IsAnimatorOnlyParameter(paramName))
            {
                RemoveAnimatorOnlyParametersFromVrc();
            }

            AnimatorControllerParameter[] parameters = controller.parameters;
            if (!_hideVrcBuiltInParameters && paramName == "BlendTreeWeight")
            {
                foreach (var parameter in parameters)
                {
                    if (parameter.name == paramName) return;
                }
            }

            foreach (var p in parameters)
            {
                if (p.name == paramName && (p.type == AnimatorControllerParameterType.Float || p.type == AnimatorControllerParameterType.Int))
                    return;
            }

            Undo.RecordObject(controller, "Ensure Float Parameter Exists");
            var list = new List<AnimatorControllerParameter>(parameters);

            AnimatorControllerParameter newParam = new AnimatorControllerParameter
            {
                name = paramName,
                type = AnimatorControllerParameterType.Float,
                defaultFloat = defaultValue > 0f ? defaultValue : 1f
            };

            list.Insert(0, newParam);
            controller.parameters = list.ToArray();
            _selectedParamIndex = 0;
            EditorUtility.SetDirty(controller);

            if (_vrcExpressionParametersAsset != null)
            {
                AddVrcParameter(controller, _vrcExpressionParametersAsset, paramName);
            }
        }

        private string GetOrCreateFirstFloatParameter()
        {
            AnimatorController controller = GetAnimatorController();
            if (controller == null) return "BlendTreeWeight";

            AnimatorControllerParameter[] parameters = controller.parameters;
            var list = new List<AnimatorControllerParameter>(parameters);

            int namedWeightIdx = list.FindIndex(p => p.name == "BlendTreeWeight");
            if (_hideVrcBuiltInParameters && namedWeightIdx != -1)
            {
                RemoveAnimatorOnlyParametersFromVrc();
                EnsureBlendTreeWeightFloatParameter(controller);
                list = new List<AnimatorControllerParameter>(controller.parameters);
            }

            int weightIdx = list.FindIndex(p => p.name == "BlendTreeWeight" && p.type == AnimatorControllerParameterType.Float);
            if (weightIdx != -1)
            {
                RemoveAnimatorOnlyParametersFromVrc();

                if (list[weightIdx].defaultFloat != 1f || weightIdx != 0)
                {
                    Undo.RecordObject(controller, "Normalize BlendTreeWeight Parameter");
                    list[weightIdx].defaultFloat = 1f;
                    var item = list[weightIdx];
                    list.RemoveAt(weightIdx);
                    list.Insert(0, item);
                    controller.parameters = list.ToArray();
                    EditorUtility.SetDirty(controller);
                }
                return "BlendTreeWeight";
            }

            int blendIdx = list.FindIndex(p => p.name == "Blend" && p.type == AnimatorControllerParameterType.Float);
            if (blendIdx != -1 && namedWeightIdx == -1)
            {
                Undo.RecordObject(controller, "Rename Blend to BlendTreeWeight");
                list[blendIdx].name = "BlendTreeWeight";
                list[blendIdx].defaultFloat = 1f;

                var item = list[blendIdx];
                list.RemoveAt(blendIdx);
                list.Insert(0, item);
                controller.parameters = list.ToArray();
                EditorUtility.SetDirty(controller);

                RemoveAnimatorOnlyParametersFromVrc();
                if (_vrcExpressionParametersAsset != null)
                {
                    RemoveParameterFromVRC("Blend");
                    AssetDatabase.SaveAssets();
                    AddVrcParameter(controller, _vrcExpressionParametersAsset, "BlendTreeWeight");
                }
                return "BlendTreeWeight";
            }

            foreach (var p in list)
            {
                if (p.type == AnimatorControllerParameterType.Float)
                {
                    return p.name;
                }
            }

            if (list.Exists(p => p.name == "BlendTreeWeight"))
            {
                return "BlendTreeWeight";
            }

            Undo.RecordObject(controller, "Create BlendTreeWeight Parameter");

            AnimatorControllerParameter newParam = new AnimatorControllerParameter
            {
                name = "BlendTreeWeight",
                type = AnimatorControllerParameterType.Float,
                defaultFloat = 1f
            };

            list.Insert(0, newParam);
            controller.parameters = list.ToArray();
            _selectedParamIndex = 0;
            EditorUtility.SetDirty(controller);

            RemoveAnimatorOnlyParametersFromVrc();
            if (_vrcExpressionParametersAsset != null)
            {
                AddVrcParameter(controller, _vrcExpressionParametersAsset, "BlendTreeWeight");
            }

            return "BlendTreeWeight";
        }

        private static void ClearMotionCut() { _motionCuts.Clear(); }

        private static bool MotionCutMatches(MotionCutEntry entry, ChildMotion[] children)
        {
            return !entry.invalid && entry.index >= 0 && entry.index < children.Length &&
                entry.original.Equals(children[entry.index]) &&
                (ReferenceEquals(entry.original.motion, null) || entry.original.motion != null);
        }

        private static bool IsMotionCut(BlendTree owner, int index, ChildMotion[] children)
        {
            return _motionCuts.Exists(entry => entry.owner == owner && entry.index == index && MotionCutMatches(entry, children));
        }

        private void CopyMotion(BlendTree owner, int index, bool cut)
        {
            if (owner == null) return;
            var children = owner.children;
            if (index < 0 || index >= children.Length) return;
            if (cut)
            {
                int existing = _motionCuts.FindIndex(entry => entry.owner == owner && entry.index == index);
                if (existing >= 0 && MotionCutMatches(_motionCuts[existing], children)) _motionCuts.RemoveAt(existing);
                else
                {
                    if (_motionCuts.Count == 0) _motionClipboard.Clear();
                    if (existing >= 0) _motionCuts.RemoveAt(existing);
                    _motionCuts.Add(new MotionCutEntry { owner = owner, index = index, original = children[index] });
                }
            }
            else
            {
                ClearMotionCut();
                _motionClipboard.Clear();
                _motionClipboard.Add(children[index]);
            }
            Repaint();
        }

        private static void UpdateMotionCutsAfterEdit(BlendTree owner, ChildMotion[] before, ChildMotion[] after,
            int removed = -1, int movedFrom = -1, int movedTo = -1)
        {
            for (int i = _motionCuts.Count - 1; i >= 0; i--)
            {
                var cut = _motionCuts[i];
                if (cut.owner != owner) continue;
                // An external edit must not make a queued cut target a different row.
                if (!MotionCutMatches(cut, before)) { cut.invalid = true; continue; }
                if (cut.index == removed) { _motionCuts.RemoveAt(i); continue; }
                if (removed >= 0 && cut.index > removed) cut.index--;
                if (movedFrom >= 0)
                {
                    if (cut.index == movedFrom) cut.index = movedTo;
                    else if (movedFrom < movedTo && cut.index > movedFrom && cut.index <= movedTo) cut.index--;
                    else if (movedFrom > movedTo && cut.index >= movedTo && cut.index < movedFrom) cut.index++;
                }
                if (cut.index >= after.Length) cut.invalid = true;
                else cut.original = after[cut.index];
            }
        }

        private bool CanPasteMotions(BlendTree target, out string reason)
        {
            int count = _motionCuts.Count > 0 ? _motionCuts.Count : _motionClipboard.Count;
            reason = "Copy or cut motions first. Click Cut on each motion to move several together.";
            if (target == null || count == 0) return false;
            var sources = new Dictionary<BlendTree, ChildMotion[]>();
            foreach (var cut in _motionCuts)
            {
                if (cut.owner == null)
                {
                    reason = "A cut source was removed. Cancel Cut and select the motions again.";
                    return false;
                }
                if (!sources.TryGetValue(cut.owner, out var children)) sources[cut.owner] = children = cut.owner.children;
                if (!MotionCutMatches(cut, children))
                {
                    reason = "A cut motion changed outside this list. Cancel Cut or select that motion again.";
                    return false;
                }
            }
            reason = (_motionCuts.Count > 0 ? "Move " : "Paste ") + count + " motion(s) here in the order you selected them.";
            return true;
        }

        private static bool BlendTreeContains(BlendTree tree, BlendTree target, HashSet<BlendTree> visited)
        {
            if (tree == target) return true;
            if (tree == null || !visited.Add(tree)) return false;
            foreach (var child in tree.children)
                if (child.motion is BlendTree nested && BlendTreeContains(nested, target, visited)) return true;
            return false;
        }

        private void PasteMotions(BlendTree target)
        {
            if (this == null || target == null) return;
            CancelMotionDrag();
            if (!CanPasteMotions(target, out string reason))
            {
                EditorUtility.DisplayDialog("Cannot Paste Motions", reason, "OK");
                return;
            }
            bool moving = _motionCuts.Count > 0;
            var pasted = new List<ChildMotion>();
            var changes = new Dictionary<BlendTree, List<ChildMotion>>();
            changes[target] = new List<ChildMotion>(target.children);
            var removals = new Dictionary<BlendTree, List<int>>();
            foreach (var cut in _motionCuts)
            {
                if (!changes.ContainsKey(cut.owner)) changes[cut.owner] = new List<ChildMotion>(cut.owner.children);
                if (!removals.ContainsKey(cut.owner)) removals[cut.owner] = new List<int>();
                removals[cut.owner].Add(cut.index);
                pasted.Add(changes[cut.owner][cut.index]);
            }
            if (!moving) pasted.AddRange(_motionClipboard);
            string targetPath = AssetDatabase.GetAssetPath(target);
            foreach (var child in pasted)
                if (child.motion is BlendTree tree)
                {
                    if (tree == target || (moving && BlendTreeContains(tree, target, new HashSet<BlendTree>())))
                    {
                        EditorUtility.DisplayDialog("Cannot Paste Motions", "A BlendTree cannot be moved into itself or one of its own child trees.", "OK");
                        return;
                    }
                    if (!moving && string.IsNullOrEmpty(targetPath))
                    {
                        EditorUtility.DisplayDialog("Cannot Paste Motions", "Save the destination BlendTree asset before pasting copied BlendTrees into it.", "OK");
                        return;
                    }
                }
            foreach (var removal in removals)
            {
                removal.Value.Sort();
                for (int i = removal.Value.Count - 1; i >= 0; i--) changes[removal.Key].RemoveAt(removal.Value[i]);
            }

            string undoName = moving ? "Move Motions" : "Paste Motions";
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            var created = new List<UnityEngine.Object>();
            try
            {
                foreach (var change in changes) Undo.RegisterCompleteObjectUndo(change.Key, undoName);
                if (!moving)
                {
                    var clones = new Dictionary<BlendTree, BlendTree>();
                    for (int i = 0; i < pasted.Count; i++)
                        if (pasted[i].motion is BlendTree tree)
                        {
                            var child = pasted[i];
                            child.motion = CloneBlendTreeForPaste(tree, targetPath, clones, created);
                            pasted[i] = child;
                        }
                }
                changes[target].AddRange(pasted);
                foreach (var change in changes)
                {
                    change.Key.children = change.Value.ToArray();
                    EditorUtility.SetDirty(change.Key);
                }
                Undo.FlushUndoRecordObjects();
                AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(group);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(group);
                foreach (var obj in created) if (obj != null) DestroyImmediate(obj, true);
                ClearMotionCut();
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Cannot Paste Motions", "The move was rolled back.\n\n" + exception.Message, "OK");
                return;
            }
            if (moving)
            {
                _motionClipboard.Clear();
                _motionClipboard.AddRange(pasted);
            }
            ClearMotionCut();
            _childScrollPos = new Vector2(0, Mathf.Max(0f, target.children.Length * ElementHeight - _blendTreeListHeight));
            _hasParamCache = false;
            InvalidateLayerSummaries();
            _parameterUsageScannedName = null;
            if (_hasSetupScan) _setupChecksStale = true;
            Repaint();
        }
        private void AddChildWithDefaultParam(Motion motion)
        {
            BlendTree current = CurrentTree;
            if (current == null) return;

            Undo.RecordObject(current, "Add Motion");
            var before = current.children;

            current.AddChild(motion);
            ChildMotion[] children = current.children;
            if (children.Length > 0)
            {
                string defaultParam = GetOrCreateFirstFloatParameter();
                children[children.Length - 1].directBlendParameter = defaultParam;
            }
            current.children = children;
            UpdateMotionCutsAfterEdit(current, before, current.children);
            EditorUtility.SetDirty(current);
        }

        private BlendTree CloneBlendTreeForPaste(
    BlendTree source,
    string assetPath,
    Dictionary<BlendTree, BlendTree> clones,
    List<UnityEngine.Object> created = null)
        {
            if (source == null)
                return null;

            if (clones.TryGetValue(source, out BlendTree existingClone))
                return existingClone;

            var clone = new BlendTree
            {
                name = source.name + "",
                blendType = source.blendType,
                blendParameter = source.blendParameter,
                blendParameterY = source.blendParameterY,
                useAutomaticThresholds = false,
                hideFlags = source.hideFlags
            };

            if (created != null) created.Add(clone);
            AssetDatabase.AddObjectToAsset(clone, assetPath);
            if (created != null) Undo.RegisterCreatedObjectUndo(clone, "Paste BlendTree");
            clones.Add(source, clone);

            ChildMotion[] children = source.children;

            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].motion is BlendTree nestedTree)
                {
                    children[i].motion = CloneBlendTreeForPaste(
                        nestedTree,
                        assetPath,
                        clones,
                        created);
                }
            }

            clone.children = children;
            clone.minThreshold = source.minThreshold;
            clone.maxThreshold = source.maxThreshold;
            clone.useAutomaticThresholds = source.useAutomaticThresholds;

            EditorUtility.SetDirty(clone);
            return clone;
        }

        private bool IsStandaloneBlendTreeAsset(BlendTree tree)
        {
            if (tree == null)
                return false;

            string assetPath = AssetDatabase.GetAssetPath(tree);
            return !string.IsNullOrEmpty(assetPath) &&
                   AssetDatabase.LoadMainAssetAtPath(assetPath) == tree;
        }

        private BlendTree CreateStandaloneBlendTreeAsset(BlendTree source, string assetPath)
        {
            var extracted = new BlendTree
            {
                name = source.name,
                blendType = source.blendType,
                blendParameter = source.blendParameter,
                blendParameterY = source.blendParameterY,
                useAutomaticThresholds = false,
                hideFlags = HideFlags.None
            };

            AssetDatabase.CreateAsset(extracted, assetPath);

            var clones = new Dictionary<BlendTree, BlendTree>
            {
                { source, extracted }
            };

            ChildMotion[] children = source.children;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].motion is BlendTree nestedTree)
                {
                    children[i].motion = CloneBlendTreeForPaste(
                        nestedTree,
                        assetPath,
                        clones);
                }
            }

            extracted.children = children;
            extracted.minThreshold = source.minThreshold;
            extracted.maxThreshold = source.maxThreshold;
            extracted.useAutomaticThresholds = source.useAutomaticThresholds;
            EditorUtility.SetDirty(extracted);
            return extracted;
        }

        private void ExtractBlendTreeToAsset(BlendTree source)
        {
            if (source == null || source.blendType != BlendTreeType.Direct)
                return;

            AnimatorController controller = GetAnimatorController();
            if (controller == null)
            {
                EditorUtility.DisplayDialog(
                    "Extract Blend Tree",
                    "Select an Animator Controller in Avatar Control Hub before extracting a Blend Tree.",
                    "OK");
                return;
            }

            string safeName = source.name.Replace("/", "_").Replace("\\", "_");
            string assetPath = EditorUtility.SaveFilePanelInProject(
                "Extract Blend Tree",
                safeName,
                "asset",
                "Choose where to save the standalone Blend Tree asset.");

            if (string.IsNullOrEmpty(assetPath))
                return;

            assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            BlendTree extracted;

            try
            {
                extracted = CreateStandaloneBlendTreeAsset(source, assetPath);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog(
                    "Extract Blend Tree",
                    "Unity could not create the standalone Blend Tree asset. See the Console for details.",
                    "OK");
                return;
            }

            int replacedReferences = ReplaceBlendTreeReferencesInController(
                controller,
                source,
                extracted);

            if (replacedReferences == 0)
            {
                AssetDatabase.SaveAssets();
                EditorUtility.DisplayDialog(
                    "Extract Blend Tree",
                    "The asset was created, but the selected Blend Tree was not referenced by this controller, so no Animator references were changed. The new asset has been kept.",
                    "OK");
                return;
            }

            source.hideFlags |= HideFlags.HideInHierarchy;
            EditorUtility.SetDirty(source);
            ReplaceBlendTreeReferencesInWindow(source, extracted);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            EditorApplication.RepaintProjectWindow();
            Repaint();
        }

        private void MakeInternalCopyOfExtractedBlendTree(BlendTree extracted)
        {
            if (extracted == null || extracted.blendType != BlendTreeType.Direct)
                return;

            AnimatorController controller = GetAnimatorController();
            if (controller == null)
            {
                EditorUtility.DisplayDialog(
                    "Make Internal Copy",
                    "Select the Animator Controller that uses this extracted Blend Tree.",
                    "OK");
                return;
            }

            string controllerPath = AssetDatabase.GetAssetPath(controller);
            if (string.IsNullOrEmpty(controllerPath))
                return;

            var clones = new Dictionary<BlendTree, BlendTree>();
            BlendTree internalCopy = CloneBlendTreeForPaste(
                extracted,
                controllerPath,
                clones);
            internalCopy.name = extracted.name;

            foreach (BlendTree clone in clones.Values)
            {
                clone.hideFlags |= HideFlags.HideInHierarchy;
                EditorUtility.SetDirty(clone);
            }

            int replacedReferences = ReplaceBlendTreeReferencesInController(
                controller,
                extracted,
                internalCopy);

            if (replacedReferences == 0)
            {
                AssetDatabase.SaveAssets();
                EditorUtility.DisplayDialog(
                    "Make Internal Copy",
                    "The internal copy was created, but this controller no longer references the extracted asset. The extracted asset has been kept.",
                    "OK");
                return;
            }

            ReplaceBlendTreeReferencesInWindow(extracted, internalCopy);
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            EditorApplication.RepaintProjectWindow();
            Repaint();
        }

        private int ReplaceBlendTreeReferencesInController(
            AnimatorController controller,
            BlendTree source,
            BlendTree replacement)
        {
            if (controller == null || source == null || replacement == null)
                return 0;

            int replacedReferences = 0;
            var visitedTrees = new HashSet<BlendTree>();

            foreach (AnimatorControllerLayer layer in controller.layers)
            {
                if (layer.stateMachine != null)
                {
                    replacedReferences += ReplaceBlendTreeReferencesInStateMachine(
                        layer.stateMachine,
                        source,
                        replacement,
                        visitedTrees);
                }
            }

            return replacedReferences;
        }

        private int ReplaceBlendTreeReferencesInStateMachine(
            AnimatorStateMachine stateMachine,
            BlendTree source,
            BlendTree replacement,
            HashSet<BlendTree> visitedTrees)
        {
            if (stateMachine == null)
                return 0;

            int replacedReferences = 0;

            foreach (ChildAnimatorState childState in stateMachine.states)
            {
                AnimatorState state = childState.state;
                if (state == null)
                    continue;

                if (state.motion == source)
                {
                    Undo.RecordObject(state, "Replace Blend Tree Reference");
                    state.motion = replacement;
                    EditorUtility.SetDirty(state);
                    replacedReferences++;
                }
                else if (state.motion is BlendTree rootTree)
                {
                    replacedReferences += ReplaceBlendTreeReferencesInTree(
                        rootTree,
                        source,
                        replacement,
                        visitedTrees);
                }
            }

            foreach (ChildAnimatorStateMachine childStateMachine in stateMachine.stateMachines)
            {
                replacedReferences += ReplaceBlendTreeReferencesInStateMachine(
                    childStateMachine.stateMachine,
                    source,
                    replacement,
                    visitedTrees);
            }

            return replacedReferences;
        }

        private int ReplaceBlendTreeReferencesInTree(
            BlendTree parent,
            BlendTree source,
            BlendTree replacement,
            HashSet<BlendTree> visitedTrees)
        {
            if (parent == null || !visitedTrees.Add(parent))
                return 0;

            ChildMotion[] children = parent.children;
            int replacedReferences = 0;
            bool changed = false;

            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].motion == source)
                {
                    children[i].motion = replacement;
                    replacedReferences++;
                    changed = true;
                }
                else if (children[i].motion is BlendTree nestedTree)
                {
                    replacedReferences += ReplaceBlendTreeReferencesInTree(
                        nestedTree,
                        source,
                        replacement,
                        visitedTrees);
                }
            }

            if (changed)
            {
                Undo.RecordObject(parent, "Replace Blend Tree Reference");
                parent.children = children;
                EditorUtility.SetDirty(parent);
            }

            return replacedReferences;
        }

        private void ReplaceBlendTreeReferencesInWindow(BlendTree source, BlendTree replacement)
        {
            for (int i = 0; i < _navigationPath.Count; i++)
            {
                if (_navigationPath[i] == source)
                    _navigationPath[i] = replacement;
            }

            for (int i = 0; i < _rootTrees.Count; i++)
            {
                if (_rootTrees[i] == source)
                    _rootTrees[i] = replacement;
            }
        }
        private static string GetBlendTreeNamingParameter(BlendTree tree)
        {
            if (tree == null || tree.blendType == BlendTreeType.Direct) return null;
            return string.IsNullOrWhiteSpace(tree.blendParameter) ? null : tree.blendParameter;
        }

        private void RenameChildBlendTreesFromParameters(BlendTree parent)
        {
            if (this == null || parent == null || CurrentTree != parent) return;
            var renames = new List<KeyValuePair<BlendTree, string>>();
            var seen = new HashSet<BlendTree>();
            int skipped = 0;
            foreach (var child in parent.children)
            {
                if (!(child.motion is BlendTree tree) || tree == parent || !seen.Add(tree)) continue;
                if (tree.blendType == BlendTreeType.Direct) continue;
                string parameter = GetBlendTreeNamingParameter(tree);
                if (parameter == null) { skipped++; continue; }
                if (tree.name != parameter) renames.Add(new KeyValuePair<BlendTree, string>(tree, parameter));
            }

            const string title = "Name BlendTrees from Parameters";
            if (renames.Count == 0)
            {
                EditorUtility.DisplayDialog(title,
                    "No names to change in '" + parent.name + "'.\n\n" +
                    "Child BlendTrees already match their parameter, or have no usable naming parameter. " +
                    "Direct BlendTrees are excluded.", "OK");
                return;
            }

            var message = new System.Text.StringBuilder("Rename ").Append(renames.Count)
                .Append(" child BlendTree(s) in '").Append(parent.name)
                .Append("' using their own internal parameter?\n\nScope: the immediate child BlendTrees shown in this list. " +
                    "2D trees use their X parameter. Direct BlendTrees are excluded.\n\n");
            for (int i = 0; i < Math.Min(renames.Count, 10); i++)
                message.Append(renames[i].Key.name).Append(" → ").Append(renames[i].Value).Append('\n');
            if (renames.Count > 10) message.Append("… and ").Append(renames.Count - 10).Append(" more.\n");
            if (skipped > 0) message.Append('\n').Append(skipped)
                .Append(" tree(s) with no usable naming parameter will be skipped.\n");
            message.Append("\nRename these BlendTree assets? This supports Undo.");
            if (!EditorUtility.DisplayDialog(title, message.ToString(), "Yes", "No")) return;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(title);
            var targets = new UnityEngine.Object[renames.Count];
            for (int i = 0; i < renames.Count; i++) targets[i] = renames[i].Key;
            Undo.RegisterCompleteObjectUndo(targets, title);
            foreach (var rename in renames)
            {
                rename.Key.name = rename.Value;
                EditorUtility.SetDirty(rename.Key);
            }
            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(undoGroup);
            InvalidateLayerSummaries();
            _parameterUsageScannedName = null;
            if (_hasSetupScan) _setupChecksStale = true;
            Repaint();
        }

        private void DrawChildMotions()
        {
            BlendTree current = CurrentTree;
            if (_isDragging && _motionDragTree != current) CancelMotionDrag();
            if (current == null) return;
            ChildMotion[] children = current.children;

            GUILayout.Space(15);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Label($"Child Motions ({children.Length})", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (HubDropdownButtonWithId("BlendTreeOptions_" + current.GetInstanceID(), new GUIContent("Options ▾", "Rename or manage this BlendTree."), GUI.skin.button, GUILayout.Width(90)))
            {
                var options = new HubDropdownMenu(this);
                var renameLabel = new GUIContent("Name from Params",
                    "Rename the child BlendTrees in this list using each tree's own parameter (X for 2D). Direct BlendTrees are excluded. Shows a Yes/No confirmation.");
                if (Array.Exists(children, child => child.motion is BlendTree tree && tree != current && tree.blendType != BlendTreeType.Direct))
                    options.AddItem(renameLabel, false,
                        () => EditorApplication.delayCall += () => RenameChildBlendTreesFromParameters(current));
                else options.AddDisabledItem(renameLabel);

                if (current.blendType == BlendTreeType.Direct)
                {
                    options.AddSeparator("");
                    bool isExtracted = IsStandaloneBlendTreeAsset(current);
                    var assetLabel = new GUIContent(isExtracted ? "Make Internal Copy" : "Extract Blend Tree");
                    if (GetAnimatorController() == null) options.AddDisabledItem(assetLabel);
                    else options.AddItem(assetLabel, false, () =>
                    {
                        if (current == null || CurrentTree != current) return;
                        if (isExtracted) MakeInternalCopyOfExtractedBlendTree(current);
                        else ExtractBlendTreeToAsset(current);
                    });
                }
                options.ShowAsContext();
            }

            if (GUILayout.Button("+ Add Motion", GUILayout.Width(100)))
            {
                EditorGUILayout.EndHorizontal();
                AddChildWithDefaultParam(null);
                return;
            }

            if (GUILayout.Button("+ Add Blend Tree", GUILayout.Width(130)))
            {
                EditorGUILayout.EndHorizontal();
                BlendTree newSubTree = new BlendTree { name = "New Blend Tree", blendType = BlendTreeType.Simple1D, hideFlags = HideFlags.HideInHierarchy };

                string assetPath = AssetDatabase.GetAssetPath(current);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    AssetDatabase.AddObjectToAsset(newSubTree, assetPath);
                    AssetDatabase.SaveAssets();
                }
                AddChildWithDefaultParam(newSubTree);
                return;
            }

            if (_motionCuts.Count > 0 && GUILayout.Button(new GUIContent("Cancel Cut", "Clear all queued cuts."), GUILayout.Width(80)))
                ClearMotionCut();
            int pasteCount = _motionCuts.Count > 0 ? _motionCuts.Count : _motionClipboard.Count;
            bool canPaste = CanPasteMotions(current, out string pasteReason);
            using (new EditorGUI.DisabledScope(!canPaste))
                if (GUILayout.Button(new GUIContent(pasteCount > 1 ? "Paste (" + pasteCount + ")" : "Paste", pasteReason),
                    GUILayout.Width(pasteCount > 1 ? 76 : 50)))
                    EditorApplication.delayCall += () => PasteMotions(current);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(10);

            if (children.Length == 0)
            {
                EditorGUILayout.HelpBox("No motions added yet.", MessageType.None);
                return;
            }

            Event e = Event.current;

            using var childScrollView = new NestedScrollViewScope(ref _childScrollPos, GUILayout.Height(_blendTreeListHeight));

            int startIndex = Mathf.Max(0, Mathf.FloorToInt(_childScrollPos.y / ElementHeight));
            int visibleCount = 50;
            int endIndex = Mathf.Min(startIndex + visibleCount, children.Length);

            if (startIndex > 0) GUILayout.Space(startIndex * ElementHeight);

            int indexToRemove = -1;
            int motionToCopy = -1, motionToCut = -1;
            HashSet<AnimationClip> generatedClipsToDelete = null;
            bool wantsToOpen = false;
            BlendTree treeToOpen = null;

            EditorGUI.BeginChangeCheck();

            for (int i = startIndex; i < endIndex; i++)
            {
                Color originalBgColor = GUI.backgroundColor;
                Color originalColor = GUI.color;

                if (_isDragging && _dragSourceIndex == i)
                {
                    GUI.backgroundColor = new Color(0.3f, 0.7f, 1f, 1f);
                }

                bool isCut = IsMotionCut(current, i, children);
                if (isCut)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.4f);
                }

                EditorGUILayout.BeginHorizontal(GUILayout.Height(ElementHeight));

                GUILayout.Label(new GUIContent("≡", ReorderDragTooltip), EditorStyles.boldLabel, GUILayout.Width(15));
                Rect handleRect = GUILayoutUtility.GetLastRect();
                EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.Pan);

                if (e.type == EventType.MouseDown && e.button == 0 && handleRect.Contains(e.mousePosition))
                {
                    _dragSourceIndex = i;
                    _isDragging = true;
                    _motionDragTree = current;
                    _motionDragOriginalChildren = current.children;
                    _motionDragHasMoved = false;
                    _motionDragOriginalCutIndices = new Dictionary<MotionCutEntry, int>();
                    foreach (var cut in _motionCuts)
                        if (cut.owner == current && MotionCutMatches(cut, children)) _motionDragOriginalCutIndices[cut] = cut.index;
                    GUI.FocusControl(null);
                    e.Use();
                }

                GUILayout.Label(i.ToString(), GUILayout.Width(25));

                children[i].motion = (Motion)EditorGUILayout.ObjectField(children[i].motion, typeof(Motion), false, GUILayout.ExpandWidth(true));

                if (children[i].motion is BlendTree subTree)
                {
                    if (GUILayout.Button("Open", GUILayout.Width(45)))
                    {
                        wantsToOpen = true;
                        treeToOpen = subTree;
                    }
                }
                else
                {
                    GUILayout.Space(49);
                }

                if (current.blendType == BlendTreeType.Direct)
                {
                    GUILayout.Label("Param:", GUILayout.Width(45));

                    string paramId = "BTChildParam_" + i;
                    children[i].directBlendParameter = DrawFlexibleParameterComboBox(paramId, children[i].directBlendParameter);
                }
                else if (Is2D(current.blendType))
                {
                    GUILayout.Label("X:", GUILayout.Width(15));
                    Vector2 pos = children[i].position;
                    pos.x = EditorGUILayout.FloatField(pos.x, GUILayout.Width(40));
                    GUILayout.Label("Y:", GUILayout.Width(15));
                    pos.y = EditorGUILayout.FloatField(pos.y, GUILayout.Width(40));
                    children[i].position = pos;
                }
                else
                {
                    GUILayout.Label("Threshold:", GUILayout.Width(65));
                    bool autoThresh = current.useAutomaticThresholds;
                    if (autoThresh) EditorGUI.BeginDisabledGroup(true);
                    children[i].threshold = EditorGUILayout.FloatField(children[i].threshold, GUILayout.Width(50));
                    if (autoThresh) EditorGUI.EndDisabledGroup();
                }

                GUILayout.Label("Spd:", GUILayout.Width(30));
                children[i].timeScale = EditorGUILayout.FloatField(children[i].timeScale, GUILayout.Width(40));

                if (GUILayout.Button("C", GUILayout.Width(22)))
                {
                    motionToCopy = i;
                }

                if (GUILayout.Button(new GUIContent("Cut", isCut ? "Remove this motion from the cut batch." :
                    "Add this motion to the cut batch. Cut more motions, then paste them together."), GUILayout.Width(35)))
                {
                    motionToCut = i;
                }

                GUI.backgroundColor = new Color(1f, 0.4f, 0.4f);
                if (GUILayout.Button("X", GUILayout.Width(22)))
                {
                    indexToRemove = i;
                }
                GUI.backgroundColor = originalBgColor;

                EditorGUILayout.EndHorizontal();

                GUI.backgroundColor = originalBgColor;
                GUI.color = originalColor;
            }

            int remaining = children.Length - endIndex;
            if (remaining > 0) GUILayout.Space(remaining * ElementHeight);

            if (_isDragging && _dragSourceIndex >= 0 && _dragSourceIndex < children.Length)
            {
                if (e.type == EventType.MouseDrag)
                {
                    int hoverIndex = Mathf.FloorToInt(e.mousePosition.y / 26f);
                    hoverIndex = Mathf.Clamp(hoverIndex, 0, children.Length - 1);

                    if (hoverIndex != _dragSourceIndex)
                    {
                        if (!_motionDragHasMoved) Undo.RegisterCompleteObjectUndo(current, "Reorder Motions");
                        var list = new List<ChildMotion>(children);
                        var item = list[_dragSourceIndex];
                        list.RemoveAt(_dragSourceIndex);
                        list.Insert(hoverIndex, item);

                        children = list.ToArray();
                        var beforeMove = current.children;
                        current.children = children;
                        UpdateMotionCutsAfterEdit(current, beforeMove, current.children, movedFrom: _dragSourceIndex, movedTo: hoverIndex);
                        _dragSourceIndex = hoverIndex;
                        _motionDragHasMoved = true;
                        Repaint();
                    }
                    e.Use();
                }
                else if (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp)
                {
                    ResetMotionDrag();
                    EditorUtility.SetDirty(current);
                    Repaint();
                    e.Use();
                }
            }

            childScrollView.Dispose();

            DrawBlendTreeListResizer();

            if (wantsToOpen)
            {
                GUI.FocusControl(null);
                _navigationPath.Add(treeToOpen);
                GUIUtility.ExitGUI();
            }

            if (indexToRemove >= 0)
            {
                Undo.RecordObject(current, "Remove Motion");
                var beforeRemove = current.children;

                BlendTree removedTree = children[indexToRemove].motion as BlendTree;
                if (IsGeneratedConverterBlendTree(removedTree))
                {
                    generatedClipsToDelete = new HashSet<AnimationClip>();
                    CollectGeneratedConverterClips(removedTree, generatedClipsToDelete, new HashSet<BlendTree>());
                }

                current.RemoveChild(indexToRemove);
                children = current.children;
                UpdateMotionCutsAfterEdit(current, beforeRemove, children, removed: indexToRemove);
                if (generatedClipsToDelete != null)
                    Undo.DestroyObjectImmediate(removedTree);
            }

            if (EditorGUI.EndChangeCheck() || indexToRemove >= 0)
            {
                var beforeEdit = current.children;
                current.children = children;
                UpdateMotionCutsAfterEdit(current, beforeEdit, current.children);
                EditorUtility.SetDirty(current);
                DeleteGeneratedConverterClipAssets(generatedClipsToDelete);
            }
            if (motionToCopy >= 0) CopyMotion(current, motionToCopy, false);
            if (motionToCut >= 0) CopyMotion(current, motionToCut, true);
        }

        private void RefreshAvailableTrees()
        {
            _rootTrees.Clear();
            _navigationPath.Clear();

            if (_targetAsset == null) return;

            if (_targetAsset is BlendTree bt)
            {
                _rootTrees.Add(bt);
            }
            else if (_targetAsset is RuntimeAnimatorController)
            {
                AnimatorController controller = _targetAsset as AnimatorController;
                if (controller == null)
                {
                    string path = AssetDatabase.GetAssetPath(_targetAsset);
                    if (!string.IsNullOrEmpty(path))
                    {
                        controller = AssetDatabase.LoadMainAssetAtPath(path) as AnimatorController;
                    }
                }

                if (controller != null)
                {
                    HashSet<BlendTree> activeTrees = new HashSet<BlendTree>();

                    foreach (var layer in controller.layers)
                    {
                        if (layer.stateMachine != null)
                        {
                            SearchStateMachine(layer.stateMachine, activeTrees);
                        }
                    }

                    _rootTrees.AddRange(activeTrees);
                }

                _rootTrees.Sort((a, b) =>
                {
                    bool aIsDirect = a.blendType == BlendTreeType.Direct;
                    bool bIsDirect = b.blendType == BlendTreeType.Direct;

                    if (aIsDirect && !bIsDirect) return -1;
                    if (!aIsDirect && bIsDirect) return 1;

                    return string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase);
                });
            }
        }

        private void SearchStateMachine(AnimatorStateMachine sm, HashSet<BlendTree> activeTrees)
        {
            if (sm == null) return;

            foreach (var childState in sm.states)
            {
                if (childState.state.motion is BlendTree bt)
                {
                    activeTrees.Add(bt);
                }
            }

            foreach (var childSm in sm.stateMachines)
            {
                SearchStateMachine(childSm.stateMachine, activeTrees);
            }
        }

        private bool Is2D(BlendTreeType type)
        {
            return type == BlendTreeType.SimpleDirectional2D ||
                   type == BlendTreeType.FreeformDirectional2D ||
                   type == BlendTreeType.FreeformCartesian2D;
        }

        private void GuiLine(int i_height = 1)
        {
            Rect rect = EditorGUILayout.GetControlRect(false, i_height);
            rect.height = i_height;
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 1));
        }
    }
}
#endif
