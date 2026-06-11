using romangfit.Models;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace romangfit.Services;

public class WorkoutStateService
{
    private readonly IIndexedDbService _db;
    private readonly ILocalStorageService _localStorage;
    private readonly FirebaseService _firebase;
    private readonly HttpClient _http;
    
    private const string HistoryKey = "workout_history";
    private const string RoutinesKey = "workout_routines";
    private const string DefinitionsKey = "exercise_definitions";

    private System.Threading.Timer? _workoutTicker;
    private System.Threading.Timer? _restTicker;

    public WorkoutStateService(IIndexedDbService db, ILocalStorageService localStorage, FirebaseService firebase, HttpClient http)
    {
        _db = db;
        _localStorage = localStorage;
        _firebase = firebase;
        _http = http;
    }

    #region [전역 상태 프로퍼티]

    public string CurrentView { get; private set; } = "Dashboard";
    public DailyWorkout? ActiveWorkout { get; private set; }
    public WorkoutRoutine? SelectedRoutine { get; private set; }
    public List<ExerciseDefinition> CheckedExercises { get; private set; } = new();
    public TimeSpan ElapsedTime { get; private set; } = TimeSpan.Zero;
    public bool IsTimerOn { get; private set; } = true;

    // Firebase 상태 정보
    public FirebaseUser? CurrentUser { get; private set; }
    public bool IsFirebaseConfigured => _firebase.IsInitialized;
    public bool IsFirebaseLoggedIn => CurrentUser != null;

    // 브라우저 IndexedDB에서 불러온 마스터 컬렉션
    public List<DailyWorkout> WorkoutHistory { get; private set; } = new();
    public List<WorkoutRoutine> Routines { get; private set; } = new();
    public List<ExerciseDefinition> ExerciseDefinitions { get; private set; } = new();

    // 루틴 편집/생성을 제어하는 임시 버퍼 상태 변수들
    public WorkoutRoutine? EditingRoutine { get; private set; }
    public bool IsNewRoutine { get; private set; }

    // ⏱️ [신규] 자동 휴식 타이머 상태
    public int RestTimeRemaining { get; private set; } = 0; // 초 단위
    public int DefaultRestDuration { get; set; } = 90; // 기본 90초
    public bool IsRestTimerActive => RestTimeRemaining > 0;

    public event Action? OnStateChanged;

    #endregion

    #region [초기화 및 마이그레이션 로직]

    /// <summary>
    /// 앱 구동 시 최초 1회 Firebase 및 IndexedDB에서 데이터를 로드하고 동기화합니다.
    /// </summary>
    public async Task InitializeAsync()
    {
        // 1. Firebase 초기 설정 로드
        await _firebase.CheckAndInitializeAsync();
        CurrentUser = await _firebase.GetCurrentUserAsync();

        // 2. Firebase가 활성화 상태라면 우선적으로 운동 정의(메타데이터)를 클라우드 DB에서 로드
        if (_firebase.IsInitialized)
        {
            try
            {
                var dbDefs = await _firebase.GetDocumentsAsync<ExerciseDefinition>(DefinitionsKey);
                if (dbDefs != null && dbDefs.Any())
                {
                    ExerciseDefinitions = dbDefs;
                    // 로컬 IndexedDB에도 최신 데이터 캐싱
                    await _db.ClearAsync(DefinitionsKey);
                    await _db.PutBatchAsync(DefinitionsKey, ExerciseDefinitions);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching exercise definitions from Firebase on initialization: {ex.Message}");
            }
        }

        // 3. Firebase 로드에 실패했거나 오프라인 모드일 때 로컬 IndexedDB 백업 복구
        if (ExerciseDefinitions == null || !ExerciseDefinitions.Any())
        {
            ExerciseDefinitions = await _db.GetAllAsync<ExerciseDefinition>(DefinitionsKey);
            if (!ExerciseDefinitions.Any())
            {
                // 로컬 IndexedDB에도 없으면 health_meta.txt 파싱 로컬 시드 적재
                await SeedFromHealthMetaTxtAsync();
                await _db.PutBatchAsync(DefinitionsKey, ExerciseDefinitions);
            }
        }

        if (CurrentUser != null)
        {
            // 4-A. 로그인 상태: Firebase 실시간 클라우드 DB 동기화 진행
            await SyncWithFirebaseAsync();
        }
        else
        {
            // 4-B. 비로그인/오프라인 상태: 기존 IndexedDB 로딩
            WorkoutHistory = await _db.GetAllAsync<DailyWorkout>(HistoryKey);
            var legacyHistory = await _localStorage.GetItemAsync<List<DailyWorkout>>(HistoryKey);
            if (legacyHistory != null && legacyHistory.Any())
            {
                var existingIds = new HashSet<string>(WorkoutHistory.Select(h => h.Id));
                var toMigrate = legacyHistory.Where(h => !existingIds.Contains(h.Id)).ToList();
                if (toMigrate.Any())
                {
                    await _db.PutBatchAsync(HistoryKey, toMigrate);
                    WorkoutHistory = await _db.GetAllAsync<DailyWorkout>(HistoryKey);
                }
                await _localStorage.RemoveItemAsync(HistoryKey);
            }

            Routines = await _db.GetAllAsync<WorkoutRoutine>(RoutinesKey);
            var legacyRoutines = await _localStorage.GetItemAsync<List<WorkoutRoutine>>(RoutinesKey);
            if (legacyRoutines != null && legacyRoutines.Any())
            {
                var existingIds = new HashSet<string>(Routines.Select(r => r.Id));
                var toMigrate = legacyRoutines.Where(r => !existingIds.Contains(r.Id)).ToList();
                if (toMigrate.Any())
                {
                    await _db.PutBatchAsync(RoutinesKey, toMigrate);
                    Routines = await _db.GetAllAsync<WorkoutRoutine>(RoutinesKey);
                }
                await _localStorage.RemoveItemAsync(RoutinesKey);
            }

            if (!Routines.Any())
            {
                //SeedDefaultRoutines();
                await _db.PutBatchAsync(RoutinesKey, Routines);
            }
        }

        NotifyStateChanged();
    }

    #endregion

    #region [내비게이션 및 세션 엔지니어링]

    public void ChangeView(string targetView)
    {
        CurrentView = targetView;
        if (targetView == "ActiveLogging" && _workoutTicker == null)
        {
            StartGlobalTimer();
        }
        NotifyStateChanged();
    }

    public void PreviewRoutine(WorkoutRoutine routine)
    {
        SelectedRoutine = routine;
        ChangeView("RoutinePreview");
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
        ChangeView("ExerciseSearch");
    }

    public async Task StartWorkoutFromRoutineAsync()
    {
        if (SelectedRoutine == null) return;

        var activeExercises = new List<Exercise>();
        int setIndex = 1;

        foreach (var templateEx in SelectedRoutine.Exercises)
        {
            // 과거 내역에서 해당 운동의 직전 성공 세트 기록을 제안
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
        ChangeView("ActiveLogging");
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
        // 시나리오 A: 루틴 편집 빌더 도중 운동 추가
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
            ChangeView("RoutineEdit");
            return;
        }

        // 시나리오 B: 일반 실전 기록 기록 도중 운동 추가
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
        ChangeView("ActiveLogging");
    }

    /// <summary>
    /// 운동 완료 처리 및 DB 영구 저장
    /// </summary>
    public async Task CompleteWorkoutAsync()
    {
        if (ActiveWorkout == null) return;

        // 완료되지 않은 세트는 저장 내역에서 필터링
        foreach (var ex in ActiveWorkout.Exercises)
        {
            ex.Sets = ex.Sets.Where(s => s.IsCompleted).ToList();
        }
        ActiveWorkout.Exercises = ActiveWorkout.Exercises.Where(e => e.Sets.Any()).ToList();

        if (ActiveWorkout.Exercises.Any())
        {
            ActiveWorkout.Date = DateTime.Today; // 완료 일자 고정
            
            // Firebase 로그인 상태 시 클라우드 저장 수행
            if (IsFirebaseLoggedIn)
            {
                ActiveWorkout.UserId = CurrentUser!.Uid;
                await _firebase.SetDocumentAsync(HistoryKey, ActiveWorkout.Id, ActiveWorkout);
            }

            WorkoutHistory.Add(ActiveWorkout);
            await _db.PutAsync(HistoryKey, ActiveWorkout);
        }

        StopGlobalTimer();
        StopRestTimer();
        ActiveWorkout = null;
        ChangeView("Dashboard");
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
        ChangeView("RoutineEdit");
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
        ChangeView("RoutineEdit");
    }

    public async Task SaveEditingRoutineAsync()
    {
        if (EditingRoutine == null) return;

        var categories = EditingRoutine.Exercises.Select(e => e.Category).Distinct().ToList();
        EditingRoutine.TargetSummary = categories.Any() ? string.Join(" · ", categories) : "전신";

        if (IsFirebaseLoggedIn)
        {
            EditingRoutine.UserId = CurrentUser!.Uid;
            await _firebase.SetDocumentAsync(RoutinesKey, EditingRoutine.Id, EditingRoutine);
        }

        if (IsNewRoutine)
        {
            Routines.Add(EditingRoutine);
        }
        else
        {
            var index = Routines.FindIndex(r => r.Id == EditingRoutine.Id);
            if (index != -1) Routines[index] = EditingRoutine;
        }

        await _db.PutAsync(RoutinesKey, EditingRoutine);

        EditingRoutine = null;
        ChangeView("RoutineSelect");
    }

    public async Task DeleteRoutineAsync(string id)
    {
        var target = Routines.FirstOrDefault(r => r.Id == id);
        if (target != null)
        {
            Routines.Remove(target);
            await _db.DeleteAsync(RoutinesKey, id);

            if (IsFirebaseLoggedIn)
            {
                await _firebase.DeleteDocumentAsync(RoutinesKey, id);
            }
        }
        ChangeView("RoutineSelect");
    }

    #endregion

    #region [운동 정의(메타데이터) 추가 기능]

    public async Task AddCustomExerciseDefinitionAsync(ExerciseDefinition definition)
    {
        definition.IsCustom = true;
        
        if (IsFirebaseLoggedIn)
        {
            definition.UserId = CurrentUser!.Uid;
            await _firebase.SetDocumentAsync(DefinitionsKey, definition.Id, definition);
        }

        ExerciseDefinitions.Add(definition);
        await _db.PutAsync(DefinitionsKey, definition);
        NotifyStateChanged();
    }

    public async Task DeleteCustomExerciseDefinitionAsync(string id)
    {
        var target = ExerciseDefinitions.FirstOrDefault(d => d.Id == id && d.IsCustom);
        if (target != null)
        {
            ExerciseDefinitions.Remove(target);
            await _db.DeleteAsync(DefinitionsKey, id);

            if (IsFirebaseLoggedIn)
            {
                await _firebase.DeleteDocumentAsync(DefinitionsKey, id);
            }
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
            if (CurrentView == "ActiveLogging")
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

    // ⏱️ 쉬는시간 타이머 트리거
    public void StartRestTimer(int durationSeconds)
    {
        if (!IsTimerOn) return; // 타이머 비활성화 시 작동 방지
        
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

    #region [초기 시드 데이터 생성기]

    private void SeedDefaultExerciseDefinitions()
    {
        ExerciseDefinitions = new List<ExerciseDefinition>
        {
            // 하체
            new() { Name = "스쿼트", Category = "하체", TargetMuscles = "대퇴사두근, 둔근", Icon = "🏋️", Description = "바벨을 등 뒤에 얹고 무릎을 굽혀 앉았다 일어나는 하체 핵심 다관절 운동입니다." },
            new() { Name = "레그 프레스", Category = "하체", TargetMuscles = "대퇴사두근, 대둔근", Icon = "🦵", Description = "머신의 발판을 발로 밀어 고중량으로 하체 앞쪽과 엉덩이를 강화합니다." },
            new() { Name = "레그 컬", Category = "하체", TargetMuscles = "햄스트링 (허벅지 뒤)", Icon = "🦵", Description = "머신에 누워 다리를 접어 허벅지 뒷근육을 집중 고립 수축합니다." },
            new() { Name = "레그 익스텐션", Category = "하체", TargetMuscles = "대퇴사두근 (허벅지 앞)", Icon = "🦵", Description = "앉아서 다리를 펴며 허벅지 앞쪽 근육을 고립 발달시킵니다." },
            
            // 가슴
            new() { Name = "벤치프레스", Category = "가슴", TargetMuscles = "대흉근, 삼두근, 전면삼각근", Icon = "🏋️", Description = "누워서 바벨을 들어올리며 상체 앞쪽 볼륨을 키우는 3대 핵심 운동입니다." },
            new() { Name = "인클라인 덤벨 프레스", Category = "가슴", TargetMuscles = "상부 대흉근", Icon = "🏋️", Description = "경사 벤치에 누워 덤벨을 밀며 윗가슴 라인을 발달시킵니다." },
            new() { Name = "덤벨 플라이", Category = "가슴", TargetMuscles = "대흉근 안쪽", Icon = "👐", Description = "덤벨을 양 옆으로 벌렸다가 가슴을 모아주는 안쪽 수축 집중 운동입니다." },

            // 등
            new() { Name = "데드리프트", Category = "등", TargetMuscles = "척추기립근, 둔근, 햄스트링", Icon = "🏋️", Description = "바닥의 바벨을 뽑아 서며 몸의 후면 사슬 전체 근력을 기르는 전신 운동입니다." },
            new() { Name = "랫 풀다운", Category = "등", TargetMuscles = "광배근, 대원근", Icon = "👐", Description = "머신의 바를 위에서 아래로 당겨 등의 너비를 넓혀주는 대표 기구 운동입니다." },
            new() { Name = "바벨 로우", Category = "등", TargetMuscles = "광배근, 승모근, 능형근", Icon = "🏋️", Description = "상체를 숙이고 바벨을 배 쪽으로 당겨 등의 두께감을 채워주는 프레스급 운동입니다." },

            // 어깨
            new() { Name = "오버헤드 프레스", Category = "어깨", TargetMuscles = "전면삼각근, 삼두근", Icon = "🏋️", Description = "바벨을 머리 위로 밀어올려 어깨의 프레임과 스트렝스를 기릅니다." },
            new() { Name = "사이드 레터럴 레이즈", Category = "어깨", TargetMuscles = "측면 삼각근", Icon = "💪", Description = "양손에 덤벨을 쥐고 옆으로 던지듯 올려 어깨의 옆 볼륨을 채워줍니다." },

            // 팔
            new() { Name = "바벨 컬", Category = "팔", TargetMuscles = "상완이두근 (알통)", Icon = "💪", Description = "바벨을 말아 올리며 팔 전면 이두근의 크기를 강화합니다." },
            new() { Name = "케이블 푸시 다운", Category = "팔", TargetMuscles = "상완삼두근 (팔 뒤)", Icon = "💪", Description = "케이블 로프나 바를 아래로 펴 누르며 삼두근을 쥐어짜는 운동입니다." },

            // 유산소
            new() { Name = "싸이클", Category = "유산소", TargetMuscles = "하체 전체, 심폐", Icon = "🚴", IsCardio = true, Description = "무릎 부담 없이 탈 수 있는 실내 고정형 자전거 유산소 운동입니다." },
            new() { Name = "트레드밀", Category = "유산소", TargetMuscles = "전신, 심폐", Icon = "🏃", IsCardio = true, Description = "러닝머신 위에서 걷거나 뛰어 심폐 지구력을 기르고 칼로리를 태웁니다." }
        };
    }

    private void SeedDefaultRoutines()
    {
        Routines = new List<WorkoutRoutine>
        {
            new() { 
                Name = "하체 밀기", 
                TargetSummary = "유산소 · 하체", 
                Exercises = new() {
                    new() { Name = "싸이클", Category = "유산소", IsCardio = true, IconPlaceholder = "🚴", Sets = new() { new() { SetNumber = 1, IsCompleted = false } } },
                    new() { Name = "레그 컬", Category = "하체", IconPlaceholder = "🦵", Sets = new() { new(){SetNumber=1, Weight=30, Reps=15}, new(){SetNumber=2, Weight=35, Reps=12} } },
                    new() { Name = "레그 프레스", Category = "하체", IconPlaceholder = "🏋️", Sets = new() { new(){SetNumber=1, Weight=80, Reps=12}, new(){SetNumber=2, Weight=100, Reps=10} } }
                }
            },
            new() { 
                Name = "등 이두 (Pull)", 
                TargetSummary = "등 · 어깨 · 팔", 
                Exercises = new() {
                    new() { Name = "랫 풀다운", Category = "등", IconPlaceholder = "👐", Sets = new() { new(){SetNumber=1, Weight=40, Reps=12}, new(){SetNumber=2, Weight=45, Reps=10} } }
                }
            },
            new() { 
                Name = "가슴 어깨 삼두 (Push)", 
                TargetSummary = "가슴 · 어깨 · 팔", 
                Exercises = new() {
                    new() { Name = "케이블 푸시 다운", Category = "팔", IconPlaceholder = "💪", Sets = new() { new(){SetNumber=1, Weight=20, Reps=15} } }
                }
            }
        };
    }

    #endregion

    #region [Firebase Auth 및 동기화 엔진]

    public async Task<bool> InitializeFirebaseAsync(string configJson)
    {
        var success = await _firebase.InitializeAsync(configJson);
        if (success)
        {
            CurrentUser = await _firebase.GetCurrentUserAsync();
            await SyncWithFirebaseAsync();
        }
        return success;
    }

    public async Task ClearFirebaseConfigAsync()
    {
        await _firebase.ClearConfigAsync();
        CurrentUser = null;
        await InitializeAsync(); // 로컬 오프라인 모드로 리로딩
    }

    public async Task<bool> FirebaseSignInAsync(string email, string password)
    {
        var user = await _firebase.SignInAsync(email, password);
        if (user != null)
        {
            CurrentUser = user;
            await SyncWithFirebaseAsync();
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
            await SyncWithFirebaseAsync();
            return true;
        }
        return false;
    }

    public async Task FirebaseSignOutAsync()
    {
        await _firebase.SignOutAsync();
        CurrentUser = null;
        await InitializeAsync(); // 로그인 해제 후 로컬 IndexedDB 데이터로 원복
    }

    public async Task SyncWithFirebaseAsync()
    {
        if (!_firebase.IsInitialized || CurrentUser == null) return;

        try
        {
            // 1. 운동 정의(definitions) 동기화
            var dbDefs = await _firebase.GetDocumentsAsync<ExerciseDefinition>(DefinitionsKey);
            
            // 로컬 health_meta.txt 파싱하여 기본 정의 목록 확보
            await SeedFromHealthMetaTxtAsync();
            
            // Firebase DB에 존재하지 않는 기본 운동 정의(Code 기준)를 추출
            var dbCodes = new HashSet<string>(dbDefs.Where(d => !string.IsNullOrEmpty(d.Code)).Select(d => d.Code!));
            var missingDefs = ExerciseDefinitions.Where(d => !dbCodes.Contains(d.Code ?? "") && !d.IsCustom).ToList();
            
            if (missingDefs.Any())
            {
                var batchList = missingDefs.Select(def => (def.Id, def)).ToList();
                await _firebase.SetDocumentsBatchAsync(DefinitionsKey, batchList);
                
                // 업로드 후 다시 가져오기
                dbDefs = await _firebase.GetDocumentsAsync<ExerciseDefinition>(DefinitionsKey);
            }

            // 추가적으로 로컬 IndexedDB에 저장되어 있던 커스텀 운동 중 Firebase에 동기화되지 않은 것 일괄 동기화
            var localDefs = await _db.GetAllAsync<ExerciseDefinition>(DefinitionsKey);
            var localCustoms = localDefs.Where(d => d.IsCustom).ToList();
            var dbIds = new HashSet<string>(dbDefs.Select(d => d.Id));
            
            var missingCustoms = new List<(string Id, ExerciseDefinition Data)>();
            foreach (var custom in localCustoms)
            {
                if (!dbIds.Contains(custom.Id))
                {
                    custom.UserId = CurrentUser.Uid;
                    missingCustoms.Add((custom.Id, custom));
                }
            }

            if (missingCustoms.Any())
            {
                await _firebase.SetDocumentsBatchAsync(DefinitionsKey, missingCustoms);
                // 업로드 후 다시 전체 목록 가져오기
                dbDefs = await _firebase.GetDocumentsAsync<ExerciseDefinition>(DefinitionsKey);
            }

            // 전체 운동 정의(기본 + 커스텀) 로컬 캐시 갱신
            ExerciseDefinitions = dbDefs;
            await _db.ClearAsync(DefinitionsKey);
            await _db.PutBatchAsync(DefinitionsKey, ExerciseDefinitions);

            // 2. 루틴(routines) 동기화
            var dbRoutines = await _firebase.GetUserDocumentsAsync<WorkoutRoutine>(RoutinesKey, CurrentUser.Uid);
            var localRoutines = await _db.GetAllAsync<WorkoutRoutine>(RoutinesKey);
            
            var dbRoutineIds = new HashSet<string>(dbRoutines.Select(r => r.Id));
            foreach (var r in localRoutines)
            {
                if (!dbRoutineIds.Contains(r.Id))
                {
                    r.UserId = CurrentUser.Uid;
                    await _firebase.SetDocumentAsync(RoutinesKey, r.Id, r);
                    dbRoutines.Add(r);
                }
            }
            Routines = dbRoutines;
            await _db.ClearAsync(RoutinesKey);
            await _db.PutBatchAsync(RoutinesKey, Routines);

            // 3. 운동 일지(history) 동기화
            var dbHistory = await _firebase.GetUserDocumentsAsync<DailyWorkout>(HistoryKey, CurrentUser.Uid);
            var localHistory = await _db.GetAllAsync<DailyWorkout>(HistoryKey);

            var dbHistoryIds = new HashSet<string>(dbHistory.Select(h => h.Id));
            foreach (var h in localHistory)
            {
                if (!dbHistoryIds.Contains(h.Id))
                {
                    h.UserId = CurrentUser.Uid;
                    await _firebase.SetDocumentAsync(HistoryKey, h.Id, h);
                    dbHistory.Add(h);
                }
            }
            WorkoutHistory = dbHistory;
            await _db.ClearAsync(HistoryKey);
            await _db.PutBatchAsync(HistoryKey, WorkoutHistory);

            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during SyncWithFirebase: {ex.Message}");
        }
    }

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
                        Category = currentCategory,
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
            //SeedDefaultExerciseDefinitions();
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