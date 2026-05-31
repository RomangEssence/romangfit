using System.Text.Json;
using Microsoft.JSInterop;
using romangfit.Models;

namespace romangfit.Services;

public class WorkoutStateService
{
    private readonly IJSRuntime _js;
    public WorkoutStateService(IJSRuntime js) => _js = js;

    // 현재 앱의 메인 내비게이션 뷰 상태 관리
    // "Dashboard" | "RoutineSelect" | "ExerciseSearch" | "RoutinePreview" | "ActiveLogging"
    public string CurrentViewState { get; private set; } = "Dashboard";

    public DailyWorkout? ActiveWorkout { get; private set; }
    public WorkoutRoutine? SelectedRoutinePreview { get; private set; }
    public List<Exercise> CheckedExercisesForAdd { get; private set; } = new();

    public event Action? OnStateChanged;

    public void ChangeViewState(string newState)
    {
        CurrentViewState = newState;
        NotifyStateChanged();
    }

    public void StartNewEmptyWorkout()
    {
        ActiveWorkout = new DailyWorkout();
        CheckedExercisesForAdd.Clear();
        ChangeViewState("ExerciseSearch");
    }

    public void PreviewRoutine(WorkoutRoutine routine)
    {
        SelectedRoutinePreview = routine;
        ChangeViewState("RoutinePreview");
    }

    public void StartWorkoutFromRoutine()
    {
        if (SelectedRoutinePreview == null) return;

        ActiveWorkout = new DailyWorkout
        {
            RoutineName = SelectedRoutinePreview.Name,
            Exercises = SelectedRoutinePreview.Exercises.Select(e => new Exercise
            {
                Name = e.Name,
                Category = e.Category,
                IsCardio = e.IsCardio,
                IconPlaceholder = e.IconPlaceholder,
                Sets = e.Sets.Select(s => new SetLog { SetNumber = s.SetNumber, Weight = s.Weight, Reps = s.Reps, Duration = s.Duration }).ToList()
            }).ToList()
        };

        ChangeViewState("ActiveLogging");
    }

    public void ConfirmAddExercises()
    {
        if (ActiveWorkout == null) ActiveWorkout = new DailyWorkout();

        foreach (var ex in CheckedExercisesForAdd)
        {
            var newEx = new Exercise
            {
                Name = ex.Name,
                Category = ex.Category,
                IsCardio = ex.IsCardio,
                IconPlaceholder = ex.IconPlaceholder,
                Sets = new List<SetLog> { new() { SetNumber = 1 } }
            };
            ActiveWorkout.Exercises.Add(newEx);
        }

        CheckedExercisesForAdd.Clear();
        ChangeViewState("ActiveLogging");
    }

    public async Task CompleteWorkout()
    {
        if (ActiveWorkout == null) return;

        // 로컬스토리지 저장 로직
        var existingJson = await _js.InvokeAsync<string>("localStorage.getItem", "workout_history");
        var history = string.IsNullOrEmpty(existingJson)
            ? new List<DailyWorkout>()
            : JsonSerializer.Deserialize<List<DailyWorkout>>(existingJson) ?? new();

        history.Add(ActiveWorkout);
        await _js.InvokeVoidAsync("localStorage.setItem", "workout_history", JsonSerializer.Serialize(history));

        ActiveWorkout = null;
        ChangeViewState("Dashboard");
    }

    private void NotifyStateChanged() => OnStateChanged?.Invoke();
}