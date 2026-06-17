using Microsoft.JSInterop;
using romangfit.Models;
using romangfit.Models.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace romangfit.Services;

/// <summary>
/// 운동 데이터 상태를 관리하는 핵심 서비스입니다.
/// 모든 데이터는 로컬 캐시 없이 Firebase 클라우드 DB를 직접 바라봅니다.
/// </summary>
public class WorkoutStateService(FirebaseService firebase, HttpClient http)
{
    private readonly FirebaseService _firebase = firebase;
    private readonly HttpClient _http = http;

    private const string HistoryKey = "workout_history";
    private const string RoutinesKey = "workout_routines";
    private const string DefinitionsKey = "exercise_definitions";

    private System.Threading.Timer? _workoutTicker;
    private System.Threading.Timer? _restTicker;

    #region [전역 상태 프로퍼티]

    public WorkoutView CurrentView { get; private set; } = WorkoutView.Dashboard;
    public DailyWorkout? ActiveWorkout { get; private set; }
    public WorkoutRoutine? SelectedRoutine { get; private set; }
    public List<ExerciseDefinition> CheckedExercises { get; private set; } = new();
    public TimeSpan ElapsedTime { get; private set; } = TimeSpan.Zero;
    public bool IsTimerOn { get; private set; } = true;

    // Firebase 상태 정보
    public FirebaseUser? CurrentUser { get; private set; }
    public bool IsFirebaseConfigured => _firebase.IsInitialized;
    public bool IsFirebaseLoggedIn => CurrentUser != null;

    // Firebase에서 직접 로드한 마스터 컬렉션 (비로그인 시 빈 리스트)
    public List<DailyWorkout> WorkoutHistory { get; private set; } = new();
    public List<WorkoutRoutine> Routines { get; private set; } = new();
    public List<ExerciseDefinition> ExerciseDefinitions { get; private set; } = new();

    // 루틴 편집/생성을 제어하는 임시 버퍼 상태 변수들
    public WorkoutRoutine? EditingRoutine { get; private set; }
    public bool IsNewRoutine { get; private set; }

    // ⏱️ 자동 휴식 타이머 상태
    public int RestTimeRemaining { get; private set; } = 0;
    public int DefaultRestDuration { get; set; } = 90;
    public bool IsRestTimerActive => RestTimeRemaining > 0;

    public event Action? OnStateChanged;

    #endregion

    #region [초기화 및 클라우드 로드 로직]

    /// <summary>
    /// 앱 구동 시 최초 1회 Firebase 연결 상태를 확인하고 데이터를 완전히 새로 불러옵니다.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _firebase.CheckAndInitializeAsync();
        CurrentUser = await _firebase.GetCurrentUserAsync();

        if (IsFirebaseLoggedIn)
        {
            // 로그인 상태: Firebase 클라우드 DB에서 모든 데이터를 로드합니다.
            await LoadDataFromFirebaseAsync();
        }
        else
        {
            // 비로그인/미연결 상태: 데이터를 초기화하여 화면에 빈 상태가 보이도록 유도합니다.
            WorkoutHistory = new();
            Routines = new();
            ExerciseDefinitions = new();
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// Firebase 데이터를 마스터 컬렉션으로 직접 패치합니다.
    /// </summary>
    public async Task LoadDataFromFirebaseAsync()
    {
        if (!IsFirebaseLoggedIn) return;

        try
        {
            // 1. 운동 마스터 정의 로드
            var dbDefs = await _firebase.GetDocumentsAsync<ExerciseDefinition>(DefinitionsKey);
            if (dbDefs == null || !dbDefs.Any())
            {
                await SeedFromHealthMetaTxtAsync();
                if (ExerciseDefinitions.Any())
                {
                    var batchList = ExerciseDefinitions.Select(def => (def.Id, def)).ToList();
                    await _firebase.SetDocumentsBatchAsync(DefinitionsKey, batchList);
                }
            }
            else
            {
                ExerciseDefinitions = dbDefs;
            }

            // 2. 유저 맞춤 루틴 로드
            Routines = await _firebase.GetUserDocumentsAsync<WorkoutRoutine>(RoutinesKey, CurrentUser!.Uid) ?? new();

            // 3. 유저 운동 히스토리 일지 로드
            WorkoutHistory = await _firebase.GetUserDocumentsAsync<DailyWorkout>(HistoryKey, CurrentUser!.Uid) ?? new();

            // ✨ 수동으로 데이터를 당겨왔을 때 상태 변경을 UI 컴포넌트들에 전파합니다.
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during loading data from Firebase: {ex.Message}");
        }
    }

    #endregion

    #region [내비게이션 및 세션 엔지니어링]

    public void ChangeView(WorkoutView targetView)
    {
        CurrentView = targetView;
        if (targetView == WorkoutView.ActiveLogging && _workoutTicker == null)
        {
            StartGlobalTimer();
        }
        NotifyStateChanged();
    }

    public void PreviewRoutine(WorkoutRoutine routine)
    {
        SelectedRoutine = routine;
        ChangeView(WorkoutView.RoutinePreview);
    }

    public void StartNewEmptyWorkout()
    {
        ActiveWorkout = new DailyWorkout
        {
            RoutineName = "직접 입력",
            Date = DateTime.Today
        };
        CheckedExercises.Clear();
        ElapsedTime = TimeSpan.Zero;
        ChangeView(WorkoutView.ExerciseSearch);
    }

    public async Task StartWorkoutFromRoutineAsync()
    {
        if (SelectedRoutine == null) return;

        var activeExercises = new List<Exercise>();
        int setIndex = 1;

        foreach (var templateEx in SelectedRoutine.Exercises)
        {
            var lastPerformedEx = WorkoutHistory
                .SelectMany(h => h.Exercises)
                .LastOrDefault(e => e.Name == templateEx.Name && e.Sets.Any(s => s.IsCompleted));

            var sets = new List<SetLog>();

            if (lastPerformedEx != null)
            {
                sets = lastPerformedEx.Sets.Select(s => new SetLog
                {
                    SetNumber = s.SetNumber,
                    Weight = s.Weight,
                    Reps = s.Reps,
                    IsCompleted = false
                }).ToList();
            }
            else
            {
                sets = templateEx.Sets.Select(s => new SetLog
                {
                    SetNumber = s.SetNumber,
                    Weight = s.Weight,
                    Reps = s.Reps,
                    IsCompleted = false
                }).ToList();
            }

            activeExercises.Add(new Exercise
            {
                Id = Guid.NewGuid().ToString(),
                SetNumber = setIndex++,
                Name = templateEx.Name,
                Category = templateEx.Category,
                IsCardio = templateEx.IsCardio,
                IconPlaceholder = templateEx.IconPlaceholder,
                Sets = sets
            });
        }

        ActiveWorkout = new DailyWorkout { RoutineName = SelectedRoutine.Name, Exercises = activeExercises };
        ElapsedTime = TimeSpan.Zero;
        ChangeView(WorkoutView.ActiveLogging);
    }

    public void ToggleExerciseCheck(ExerciseDefinition exDef)
    {
        if (CheckedExercises.Any(e => e.Id == exDef.Id))
        {
            CheckedExercises.RemoveAll(e => e.Id == exDef.Id);
        }
        else
        {
            CheckedExercises.Add(exDef);
        }
        NotifyStateChanged();
    }

    public void ConfirmAddExercises()
    {
        if (EditingRoutine != null)
        {
            foreach (var exDef in CheckedExercises)
            {
                EditingRoutine.Exercises.Add(new Exercise
                {
                    Id = Guid.NewGuid().ToString(),
                    SetNumber = EditingRoutine.Exercises.Count + 1,
                    Name = exDef.Name,
                    Category = exDef.Category,
                    IsCardio = exDef.IsCardio,
                    IconPlaceholder = exDef.Icon,
                    Sets = new List<SetLog> { new() { SetNumber = 1, Weight = 40, Reps = 12 } }
                });
            }
            CheckedExercises.Clear();
            ChangeView(WorkoutView.RoutineEdit);
            return;
        }

        if (ActiveWorkout == null) ActiveWorkout = new DailyWorkout();
        foreach (var exDef in CheckedExercises)
        {
            ActiveWorkout.Exercises.Add(new Exercise
            {
                Id = Guid.NewGuid().ToString(),
                SetNumber = ActiveWorkout.Exercises.Count + 1,
                Name = exDef.Name,
                Category = exDef.Category,
                IsCardio = exDef.IsCardio,
                IconPlaceholder = exDef.Icon,
                Sets = new List<SetLog> { new() { SetNumber = 1, Weight = 40, Reps = 12 } }
            });
        }
        CheckedExercises.Clear();
        ChangeView(WorkoutView.ActiveLogging);
    }

    /// <summary>
    /// 운동 완료 처리 및 Firebase 영구 저장
    /// </summary>
    public async Task CompleteWorkoutAsync()
    {
        if (ActiveWorkout == null || !IsFirebaseLoggedIn) return;

        foreach (var ex in ActiveWorkout.Exercises)
        {
            ex.Sets = ex.Sets.Where(s => s.IsCompleted).ToList();
        }
        ActiveWorkout.Exercises = ActiveWorkout.Exercises.Where(e => e.Sets.Any()).ToList();

        if (ActiveWorkout.Exercises.Any())
        {
            ActiveWorkout.Date = DateTime.Today;
            ActiveWorkout.UserId = CurrentUser!.Uid;

            // 오직 Firebase에만 저장
            await _firebase.SetDocumentAsync(HistoryKey, ActiveWorkout.Id, ActiveWorkout);
            WorkoutHistory.Add(ActiveWorkout);
        }

        StopGlobalTimer();
        StopRestTimer();
        ActiveWorkout = null;
        ChangeView(WorkoutView.Dashboard);
    }

    #endregion

    #region [루틴 편집 비즈니스 로직]

    public void StartNewRoutineCreation()
    {
        EditingRoutine = new WorkoutRoutine
        {
            Name = "새 루틴",
            TargetSummary = "전신",
            Exercises = new List<Exercise>()
        };
        IsNewRoutine = true;
        ChangeView(WorkoutView.RoutineEdit);
    }

    public void EditSelectedRoutine()
    {
        if (SelectedRoutine == null) return;

        EditingRoutine = new WorkoutRoutine
        {
            Id = SelectedRoutine.Id,
            Name = SelectedRoutine.Name,
            TargetSummary = SelectedRoutine.TargetSummary,
            Exercises = SelectedRoutine.Exercises.Select(e => new Exercise
            {
                Id = e.Id,
                SetNumber = e.SetNumber,
                Name = e.Name,
                Category = e.Category,
                IconPlaceholder = e.IconPlaceholder,
                IsCardio = e.IsCardio,
                Sets = e.Sets.Select(s => new SetLog { SetNumber = s.SetNumber, Weight = s.Weight, Reps = s.Reps }).ToList()
            }).ToList()
        };

        IsNewRoutine = false;
        ChangeView(WorkoutView.RoutineEdit);
    }

    public async Task SaveEditingRoutineAsync()
    {
        if (EditingRoutine == null || !IsFirebaseLoggedIn) return;

        var categories = EditingRoutine.Exercises.Select(e => e.Category).Distinct().ToList();
        EditingRoutine.TargetSummary = categories.Any() ? string.Join(" · ", categories) : "전신";

        EditingRoutine.UserId = CurrentUser!.Uid;

        // Firebase 저장
        await _firebase.SetDocumentAsync(RoutinesKey, EditingRoutine.Id, EditingRoutine);

        if (IsNewRoutine)
        {
            Routines.Add(EditingRoutine);
        }
        else
        {
            var index = Routines.FindIndex(r => r.Id == EditingRoutine.Id);
            if (index != -1) Routines[index] = EditingRoutine;
        }

        EditingRoutine = null;
        ChangeView(WorkoutView.RoutineSelect);
    }

    public async Task DeleteRoutineAsync(string id)
    {
        if (!IsFirebaseLoggedIn) return;

        var target = Routines.FirstOrDefault(r => r.Id == id);
        if (target != null)
        {
            Routines.Remove(target);
            await _firebase.DeleteDocumentAsync(RoutinesKey, id);
        }
        ChangeView(WorkoutView.RoutineSelect);
    }

    #endregion

    #region [운동 정의(메타데이터) 추가 기능]

    public async Task AddCustomExerciseDefinitionAsync(ExerciseDefinition definition)
    {
        if (!IsFirebaseLoggedIn) return;

        definition.IsCustom = true;
        definition.UserId = CurrentUser!.Uid;

        await _firebase.SetDocumentAsync(DefinitionsKey, definition.Id, definition);
        ExerciseDefinitions.Add(definition);
        NotifyStateChanged();
    }

    public async Task DeleteCustomExerciseDefinitionAsync(string id)
    {
        if (!IsFirebaseLoggedIn) return;

        var target = ExerciseDefinitions.FirstOrDefault(d => d.Id == id && d.IsCustom);
        if (target != null)
        {
            ExerciseDefinitions.Remove(target);
            await _firebase.DeleteDocumentAsync(DefinitionsKey, id);
            NotifyStateChanged();
        }
    }

    #endregion

    #region [순서 제어 및 세트 관리]

    public void ReorderExercise(int fromIndex, int toIndex)
    {
        if (EditingRoutine == null || fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= EditingRoutine.Exercises.Count) return;
        if (toIndex < 0 || toIndex >= EditingRoutine.Exercises.Count) return;

        var target = EditingRoutine.Exercises[fromIndex];
        EditingRoutine.Exercises.RemoveAt(fromIndex);
        EditingRoutine.Exercises.Insert(toIndex, target);

        for (int i = 0; i < EditingRoutine.Exercises.Count; i++)
        {
            EditingRoutine.Exercises[i].SetNumber = i + 1;
        }
        NotifyStateChanged();
    }

    public void RemoveExerciseFromEditing(string exerciseId)
    {
        if (EditingRoutine == null) return;
        var target = EditingRoutine.Exercises.FirstOrDefault(e => e.Id == exerciseId);
        if (target != null)
        {
            EditingRoutine.Exercises.Remove(target);
            for (int i = 0; i < EditingRoutine.Exercises.Count; i++)
            {
                EditingRoutine.Exercises[i].SetNumber = i + 1;
            }
            NotifyStateChanged();
        }
    }

    #endregion

    #region [타이머 엔진 (운동 및 쉬는시간)]

    private void StartGlobalTimer()
    {
        _workoutTicker?.Dispose();
        _workoutTicker = new System.Threading.Timer((_) =>
        {
            if (CurrentView == WorkoutView.ActiveLogging)
            {
                ElapsedTime = ElapsedTime.Add(TimeSpan.FromSeconds(1));
                NotifyStateChanged();
            }
        }, null, 0, 1000);
    }

    private void StopGlobalTimer()
    {
        _workoutTicker?.Dispose();
        _workoutTicker = null;
    }

    public void ToggleTimer()
    {
        IsTimerOn = !IsTimerOn;
        if (!IsTimerOn) StopRestTimer();
        NotifyStateChanged();
    }

    public void StartRestTimer(int durationSeconds)
    {
        if (!IsTimerOn) return;

        StopRestTimer();
        RestTimeRemaining = durationSeconds;
        NotifyStateChanged();

        _restTicker = new System.Threading.Timer((_) =>
        {
            if (RestTimeRemaining > 0)
            {
                RestTimeRemaining--;
                NotifyStateChanged();
            }
            else
            {
                StopRestTimer();
            }
        }, null, 1000, 1000);
    }

    public void StopRestTimer()
    {
        _restTicker?.Dispose();
        _restTicker = null;
        RestTimeRemaining = 0;
        NotifyStateChanged();
    }

    public void NotifyStateChanged() => OnStateChanged?.Invoke();

    #endregion

    #region [Firebase Auth 및 동기화 엔진]

    public async Task<bool> InitializeFirebaseAsync(string configJson)
    {
        var success = await _firebase.InitializeAsync(configJson);
        if (success)
        {
            CurrentUser = await _firebase.GetCurrentUserAsync();
            await LoadDataFromFirebaseAsync();
        }
        return success;
    }

    public async Task ClearFirebaseConfigAsync()
    {
        await _firebase.ClearConfigAsync();
        CurrentUser = null;
        await InitializeAsync();
    }

    public async Task<bool> FirebaseSignInAsync(string email, string password)
    {
        var user = await _firebase.SignInAsync(email, password);
        if (user != null)
        {
            CurrentUser = user;
            await LoadDataFromFirebaseAsync();
            return true;
        }
        return false;
    }

    public async Task<bool> FirebaseSignUpAsync(string email, string password)
    {
        var user = await _firebase.SignUpAsync(email, password);
        if (user != null)
        {
            CurrentUser = user;
            await LoadDataFromFirebaseAsync();
            return true;
        }
        return false;
    }

    public async Task FirebaseSignOutAsync()
    {
        await _firebase.SignOutAsync();
        CurrentUser = null;
        await InitializeAsync(); // 로그아웃 시 다시 상태를 Clear 상태로 원복
    }

    #endregion

    #region [내부 보조 메서드]

    private async Task SeedFromHealthMetaTxtAsync()
    {
        try
        {
            var content = await _http.GetStringAsync("data/health_meta.txt");
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            var list = new List<ExerciseDefinition>();
            string currentCategory = "기타";
            string currentImage = "";

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                if (trimmed.StartsWith("http"))
                {
                    currentImage = trimmed;
                    continue;
                }

                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    currentCategory = trimmed.Substring(1, trimmed.Length - 2);
                    continue;
                }

                var parts = trimmed.Split(new[] { " - " }, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    var code = parts[0].Trim();
                    var name = parts[1].Trim();

                    var refImage = "";
                    if (code == "BB_BP" && !string.IsNullOrEmpty(currentImage))
                    {
                        refImage = currentImage;
                    }

                    list.Add(new ExerciseDefinition
                    {
                        Id = Guid.NewGuid().ToString(),
                        Code = code,
                        Name = name,
                        Category = currentCategory.ToTargetMuscleFromKoName(),
                        IsCardio = currentCategory == "유산소",
                        Icon = GetCategoryEmoji(currentCategory),
                        ReferenceImage = refImage,
                        Description = $"{currentCategory} 운동인 {name}입니다.",
                        IsCustom = false
                    });
                }
            }

            ExerciseDefinitions = list;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing health_meta.txt: {ex.Message}");
        }
    }

    private string GetCategoryEmoji(string category)
    {
        return category switch
        {
            "가슴" => "🏋️",
            "등" => "👐",
            "하체" => "🦵",
            "어깨" => "💪",
            "팔" => "💪",
            "복근" => "🧘",
            "유산소" => "🏃",
            "역도" => "🏋️",
            _ => "🏋️"
        };
    }

    #endregion
}