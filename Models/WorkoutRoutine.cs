using romangfit.Models.Enums;

namespace romangfit.Models;

public class WorkoutRoutine
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public required string Name { get; set; }
    public string TargetSummary { get; set; } = "전신";
    public List<Exercise> Exercises { get; set; } = new();
}

public class Exercise
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int SetNumber { get; set; }
    public required string Name { get; set; }
    public required TargetMuscle Category { get; set; }
    public bool IsCardio { get; set; }
    public string IconPlaceholder { get; set; } = "🏋️";
    public string Memo { get; set; } = string.Empty;
    public List<SetLog> Sets { get; set; } = new();
}

public class SetLog
{
    public int SetNumber { get; set; }
    public double Weight { get; set; }
    public int Reps { get; set; }
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(10);
    public bool IsCompleted { get; set; }
}