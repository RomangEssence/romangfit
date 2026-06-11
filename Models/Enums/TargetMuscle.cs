namespace romangfit.Models.Enums
{
    public enum TargetMuscle
    {
        [EnumInfo("1", "가슴", "Chest")]
        Chest = 1,
        [EnumInfo("2", "등", "Back")]
        Back = 2,
        [EnumInfo("3", "어깨", "Shoulder")]
        Shoulder = 3,
        [EnumInfo("4", "팔", "Arm")]
        Arm = 4,
        [EnumInfo("5", "복근", "Abs")]
        Abs = 5,
        [EnumInfo("6", "하체", "Lower")]
        Lower = 6,
        [EnumInfo("7", "역도", "Weight")]
        Weight = 7,
        [EnumInfo("8", "유산소", "Cardio")]
        Cardio = 8,
        [EnumInfo("9", "기타", "Etc")]
        Etc = 9
    }
}