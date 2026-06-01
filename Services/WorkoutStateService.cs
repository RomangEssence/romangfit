using romangfit.Models;

namespace romangfit.Services;

public class WorkoutStateService
{
    private readonly ILocalStorageService _storage;
    private const string HistoryKey = "workout_history";
    private const string RoutinesKey = "workout_routines";
    private System.Threading.Timer? _ticker;

    public WorkoutStateService(ILocalStorageService storage)
    {
        _storage = storage;
    }

    #region [전역 상태 프로퍼티]

    public string CurrentView { get; private set; } = "Dashboard";
    public DailyWorkout? ActiveWorkout { get; private set; }
    public WorkoutRoutine? SelectedRoutine { get; private set; }
    public List<Exercise> CheckedExercises { get; private set; } = new();
    public TimeSpan ElapsedTime { get; private set; } = TimeSpan.Zero;
    public bool IsTimerOn { get; private set; } = true;

    // 브라우저에서 불러온 실전 데이터들이 담길 마스터 컬렉션
    public List<DailyWorkout> WorkoutHistory { get; private set; } = new();
    public List<WorkoutRoutine> Routines { get; private set; } = new();

    // 🌟 [복원] 루틴 편집/생성을 제어하는 임시 버퍼 상태 변수들
    public WorkoutRoutine? EditingRoutine { get; private set; }
    public bool IsNewRoutine { get; private set; }

    public event Action? OnStateChanged;

    #endregion

    #region [초기화 로직]

    /// <summary>
    /// 앱이 구동될 때 최초 1회 저장소에서 루틴과 히스토리를 싹 긁어옵니다.
    /// </summary>
    public async Task InitializeAsync()
    {
        // 1. 과거 운동 기록 전량 로드
        WorkoutHistory = await _storage.GetItemAsync<List<DailyWorkout>>(HistoryKey) ?? new();

        // 2. 나만의 루틴 리스트 로드
        Routines = await _storage.GetItemAsync<List<WorkoutRoutine>>(RoutinesKey) ?? new();

        // 만약 앱을 아예 처음 켜서 저장된 루틴이 없다면, 뼈대가 될 기본 분할 시드 데이터를 강제 생성(Seeding)합니다.
        if (!Routines.Any())
        {
            SeedDefaultRoutines();
            await _storage.SetItemAsync(RoutinesKey, Routines);
        }

        NotifyStateChanged();
    }

    #endregion

    #region [내비게이션 및 세션 엔지니어링]

    public void ChangeView(string targetView)
    {
        CurrentView = targetView;
        if (targetView == "ActiveLogging" && _ticker == null)
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
            // 번핏 UX 복원: 과거 내역(WorkoutHistory)을 뒤져서 이 종목의 가장 최근 성공 무게/횟수를 제안
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
                sets = templateEx.Sets.Select(s => new SetLog { SetNumber = s.SetNumber, Weight = s.Weight, Reps = s.Reps }).ToList();
            }

            activeExercises.Add(new Exercise
            {
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

    public void ToggleExerciseCheck(Exercise ex)
    {
        if (CheckedExercises.Contains(ex)) CheckedExercises.Remove(ex);
        else CheckedExercises.Add(ex);
        NotifyStateChanged();
    }

    public void ConfirmAddExercises()
    {
        // 시나리오 A: 🌟 루틴 편집 빌더(RoutineEdit) 도중 종목을 찾으러 온 경우
        if (EditingRoutine != null)
        {
            foreach (var ex in CheckedExercises)
            {
                EditingRoutine.Exercises.Add(new Exercise
                {
                    Id = Guid.NewGuid().ToString(),
                    SetNumber = EditingRoutine.Exercises.Count + 1,
                    Name = ex.Name,
                    Category = ex.Category,
                    IsCardio = ex.IsCardio,
                    IconPlaceholder = ex.IconPlaceholder,
                    Sets = new List<SetLog> { new() { SetNumber = 1, Weight = 40, Reps = 12 } }
                });
            }
            CheckedExercises.Clear();
            ChangeView("RoutineEdit"); // ➔ 💡 루틴 편집 화면으로 안전하게 복귀!
            return;
        }

        // 시나리오 B: 일반 메인 대시보드나 라이브 실전 기록 도중 추가하러 온 경우
        if (ActiveWorkout == null) ActiveWorkout = new DailyWorkout();
        foreach (var ex in CheckedExercises)
        {
            ActiveWorkout.Exercises.Add(new Exercise
            {
                Id = Guid.NewGuid().ToString(),
                SetNumber = ActiveWorkout.Exercises.Count + 1,
                Name = ex.Name,
                Category = ex.Category,
                IsCardio = ex.IsCardio,
                IconPlaceholder = ex.IconPlaceholder,
                Sets = new List<SetLog> { new() { SetNumber = 1, Weight = 40, Reps = 12 } }
            });
        }
        CheckedExercises.Clear();
        ChangeView("ActiveLogging"); // ➔ 💡 라이브 운동 화면으로 이동!
    }

    /// <summary>
    /// 운동 완료 시 실제 메모리 컬렉션에 추가하고 영속성 저장을 동시 수행합니다.
    /// </summary>
    public async Task CompleteWorkoutAsync()
    {
        if (ActiveWorkout == null) return;

        // 유효 세트 필터링 무결성 처리
        foreach (var ex in ActiveWorkout.Exercises)
        {
            ex.Sets = ex.Sets.Where(s => s.IsCompleted).ToList();
        }
        ActiveWorkout.Exercises = ActiveWorkout.Exercises.Where(e => e.Sets.Any()).ToList();

        if (ActiveWorkout.Exercises.Any())
        {
            // 메모리 컬렉션에 먼저 집어넣어 즉시 렌더링에 반영되도록 처리
            WorkoutHistory.Add(ActiveWorkout);

            // 로컬 스토리지 파일 쓰기 수행
            await _storage.SetItemAsync(HistoryKey, WorkoutHistory);
        }

        StopGlobalTimer();
        ActiveWorkout = null;
        ChangeView("Dashboard");
    }

    #endregion

    #region [🌟 복원: 루틴 생성 및 편집 비즈니스 라이프사이클 엔진]

    /// <summary>
    /// 완전히 새로운 루틴 템플릿 생성 뷰로 진입합니다. (내루틴 화면의 ＋ 버튼 연동)
    /// </summary>
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

    /// <summary>
    /// 프리뷰 화면에서 [편집]을 눌렀을 때, 원본 데이터를 복제하여 편집 버퍼로 이관합니다.
    /// </summary>
    public void EditSelectedRoutine()
    {
        if (SelectedRoutine == null) return;

        // 딥 카피(Deep Copy): 편집 중 취소할 수 있으므로 원본 데이터를 복사본으로 격리 보호합니다.
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

    /// <summary>
    /// 튜닝된 템플릿 내용을 전역 가시성 컬렉션에 갱신하고 브라우저 디스크에 영구 각인합니다.
    /// </summary>
    public async Task SaveEditingRoutineAsync()
    {
        if (EditingRoutine == null) return;

        // 종목 카테고리를 추출하여 "유산소 · 하체" 같은 타겟 요약을 동적 생성합니다.
        var categories = EditingRoutine.Exercises.Select(e => e.Category).Distinct().ToList();
        EditingRoutine.TargetSummary = categories.Any() ? string.Join(" · ", categories) : "전신";

        if (IsNewRoutine)
        {
            Routines.Add(EditingRoutine);
        }
        else
        {
            var index = Routines.FindIndex(r => r.Id == EditingRoutine.Id);
            if (index != -1) Routines[index] = EditingRoutine;
        }

        // 로컬 스토리지 반영
        await _storage.SetItemAsync(RoutinesKey, Routines);

        EditingRoutine = null;
        ChangeView("RoutineSelect");
    }

    /// <summary>
    /// 루틴 자체를 아예 파괴(삭제)합니다.
    /// </summary>
    public async Task DeleteRoutineAsync(string id)
    {
        var target = Routines.FirstOrDefault(r => r.Id == id);
        if (target != null)
        {
            Routines.Remove(target);
            await _storage.SetItemAsync(RoutinesKey, Routines);
        }
        ChangeView("RoutineSelect");
    }

    #endregion

    #region [제스처 데이터 엔지니어링]

    /// <summary>
    /// 루틴 편집 중 드래그 앤 드롭으로 종목의 물리적 순서를 바꿉니다.
    /// </summary>
    public void ReorderExercise(int fromIndex, int toIndex)
    {
        if (EditingRoutine == null || fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= EditingRoutine.Exercises.Count) return;
        if (toIndex < 0 || toIndex >= EditingRoutine.Exercises.Count) return;

        var target = EditingRoutine.Exercises[fromIndex];
        EditingRoutine.Exercises.RemoveAt(fromIndex);
        EditingRoutine.Exercises.Insert(toIndex, target);

        // 인덱스 번호(SetNumber) 순차 재정렬
        for (int i = 0; i < EditingRoutine.Exercises.Count; i++)
        {
            EditingRoutine.Exercises[i].SetNumber = i + 1;
        }
        NotifyStateChanged();
    }

    /// <summary>
    /// 루틴 빌더 안에서 특정 운동을 제거합니다. (스와이프 액션 연동)
    /// </summary>
    public void RemoveExerciseFromEditing(string exerciseId)
    {
        if (EditingRoutine == null) return;
        var target = EditingRoutine.Exercises.FirstOrDefault(e => e.Id == exerciseId);
        if (target != null)
        {
            EditingRoutine.Exercises.Remove(target);

            // 순서 번호 재동기화
            for (int i = 0; i < EditingRoutine.Exercises.Count; i++)
            {
                EditingRoutine.Exercises[i].SetNumber = i + 1;
            }
            NotifyStateChanged();
        }
    }

    #endregion

    #region [백그라운드 스레드 제어 및 시드 데이터]

    private void StartGlobalTimer()
    {
        _ticker?.Dispose();
        _ticker = new System.Threading.Timer((_) =>
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
        _ticker?.Dispose();
        _ticker = null;
    }

    public void ToggleTimer() { IsTimerOn = !IsTimerOn; NotifyStateChanged(); }

    // 외부 이벤트 결합을 위해 public 지붕 개방
    public void NotifyStateChanged() => OnStateChanged?.Invoke();

    private void SeedDefaultRoutines()
    {
        Routines = new List<WorkoutRoutine>
        {
            new() { Name = "하체 밀기", TargetSummary = "유산소 · 하체", Exercises = new() {
                new() { Name = "싸이클", Category = "유산소", IsCardio = true, IconPlaceholder = "🚴", Sets = new() { new() } },
                new() { Name = "시티드 Leg Curl", Category = "하체", IconPlaceholder = "🦵", Sets = new() { new(){SetNumber=1, Weight=30, Reps=15}, new(){SetNumber=2, Weight=35, Reps=12} } },
                new() { Name = "수평 레그 프레스", Category = "하체", IconPlaceholder = "🏋️", Sets = new() { new(){SetNumber=1, Weight=80, Reps=12}, new(){SetNumber=2, Weight=100, Reps=10} } }
            }},
            new() { Name = "등 이두 (Pull)", TargetSummary = "등 · 어깨 · 팔", Exercises = new() {
                new() { Name = "레터럴 와이드 풀다운", Category = "등", IconPlaceholder = "👐", Sets = new() { new(){SetNumber=1, Weight=40, Reps=12}, new(){SetNumber=2, Weight=45, Reps=10} } }
            }},
            new() { Name = "가슴 어깨 삼두 (Push)", TargetSummary = "가슴 · 어깨 · 팔", Exercises = new() {
                new() { Name = "케이블 푸시 다운", Category = "팔", IconPlaceholder = "💪", Sets = new() { new(){SetNumber=1, Weight=20, Reps=15} } }
            }}
        };
    }

    #endregion
}