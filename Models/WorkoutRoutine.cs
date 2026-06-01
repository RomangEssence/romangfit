namespace romangfit.Models;

public class SetLog
{
    public int SetNumber { get; set; }
    public double Weight { get; set; }
    public int Reps { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(10); // 유산소용 기본값
    public bool IsCompleted { get; set; }
}

public class Exercise
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int SetNumber { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public bool IsCardio { get; set; }
    public string IconPlaceholder { get; set; } = "🏋️";
    public string Memo { get; set; } = string.Empty;
    public List<SetLog> Sets { get; set; } = new();
}

public class WorkoutRoutine
{
    // 🌟 루틴마다 고유한 주민번호를 무조건 부여하여 스와이프 간섭을 원천 차단합니다.
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public string TargetSummary { get; set; } = "전신";
    public List<Exercise> Exercises { get; set; } = new();
}

