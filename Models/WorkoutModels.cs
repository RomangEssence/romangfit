namespace romangfit.Models;

public class SetLog
{
    public int SetNumber { get; set; }
    public double Weight { get; set; } = 40; // 기본값 예시
    public int Reps { get; set; } = 12;     // 기본값 예시
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(10); // 유산소용 기본값
    public bool IsCompleted { get; set; }
}

public class Exercise
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // 하체, 가슴, 등, 유산소 등
    public string IconPlaceholder { get; set; } = "🏋️";
    public bool IsCardio { get; set; } // 유산소 여부 분기점
    public List<SetLog> Sets { get; set; } = new();
    public string Memo { get; set; } = string.Empty;
}

public class WorkoutRoutine
{
    public string Name { get; set; } = string.Empty;
    public string TargetSummary { get; set; } = string.Empty;
    public List<Exercise> Exercises { get; set; } = new();
}

public class DailyWorkout
{
    public DateTime Date { get; set; } = DateTime.Today;
    public string RoutineName { get; set; } = "직접 입력";
    public List<Exercise> Exercises { get; set; } = new();
}