namespace romangfit.Models.Enums
{
    [AttributeUsage(AttributeTargets.Field)]
    public class EnumInfoAttribute : Attribute
    {
        public string Code { get; }
        public string KoName { get; }
        public string EnName { get; }

        public EnumInfoAttribute(string code, string koName, string enName)
        {
            Code = code;
            KoName = koName;
            EnName = enName;
        }
    }
}