using System;

namespace romangfit.Models;

public class ExerciseDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Code { get; set; } = string.Empty; // "BB_BP" 등 메타데이터 코드
    public required string Name { get; set; }
    public required string Category { get; set; } // e.g., "하체", "가슴", "등", "어깨", "팔", "유산소"
    public string TargetMuscles { get; set; } = string.Empty; // e.g., "대퇴사두, 둔근"
    public string Icon { get; set; } = "🏋️";
    public string ReferenceImage { get; set; } = string.Empty; // Empty or URL/SVG
    public string Description { get; set; } = string.Empty; // Instruction/Guide
    public bool IsCardio { get; set; } // True if cardio exercise
    public string? UserId { get; set; } // Firebase 사용자 ID (커스텀 운동의 경우)
    public bool IsCustom { get; set; } // True if created by user
}
