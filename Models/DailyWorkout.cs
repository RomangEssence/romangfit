namespace romangfit.Models;
public class DailyWorkout
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty; // Firebase 사용자 ID
    public DateTime Date { get; set; } = DateTime.Today;
    public string RoutineName { get; set; } = "직접 입력";
    public List<Exercise> Exercises { get; set; } = new();
}
