using romangfit.Models.Enums;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace romangfit.Models
{
    public static class TargetMuscleExtentions
    {
        private record TargetMuscleMeta(string Code, string KoName, string EnName);

        // 1. 정방향 캐시: 초고속 배열 인덱스 주소 참조
        private static readonly TargetMuscleMeta[] _arrayCache;
        private static readonly int _maxIndex;

        // 2. 역방향 캐시 3종 세트 (.NET 10 FrozenDictionary) ★
        private static readonly FrozenDictionary<string, TargetMuscle> _codeCache;
        private static readonly FrozenDictionary<string, TargetMuscle> _koNameCache;
        private static readonly FrozenDictionary<string, TargetMuscle> _enNameCache;

        static TargetMuscleExtentions()
        {
            var values = Enum.GetValues<TargetMuscle>();
            _maxIndex = values.Length > 0 ? values.Max(v => (int)v) : 0;
            _arrayCache = new TargetMuscleMeta[_maxIndex + 1];

            // 런타임 성능을 극대화하기 위해 미리 공간을 분리하여 임시 딕셔너리 생성
            var codeTmp = new Dictionary<string, TargetMuscle>(StringComparer.Ordinal);
            var koTmp = new Dictionary<string, TargetMuscle>(StringComparer.Ordinal);

            // 영어명은 대소문자 무시 옵션 부여
            var enTmp = new Dictionary<string, TargetMuscle>(StringComparer.OrdinalIgnoreCase);

            // 앱 시작 시 딱 한 번 리플렉션으로 4개의 캐시판을 동시에 구워버립니다.
            foreach (var status in values)
            {
                var field = typeof(TargetMuscle).GetField(status.ToString());
                var attr = field?.GetCustomAttribute<EnumInfoAttribute>();

                if (attr != null)
                {
                    // 정방향 세팅
                    _arrayCache[(int)status] = new TargetMuscleMeta(attr.Code, attr.KoName, attr.EnName);

                    // 역방향 세팅 (각각의 그릇에 매핑 주입)
                    codeTmp[attr.Code] = status;
                    koTmp[attr.KoName] = status;
                    enTmp[attr.EnName] = status;
                }
            }

            // 고성능 읽기 전용으로 프리징(Freeze)
            _codeCache = codeTmp.ToFrozenDictionary();
            _koNameCache = koTmp.ToFrozenDictionary();
            _enNameCache = enTmp.ToFrozenDictionary();
        }

        // --- ➡️ 정방향 확장 메서드 (Enum -> 문자열) ---
        public static string GetCode(this TargetMuscle type) => _arrayCache[(int)type]?.Code ?? "9";
        public static string GetKoName(this TargetMuscle type) => _arrayCache[(int)type]?.KoName ?? type.ToString();
        public static string GetEnName(this TargetMuscle type) => _arrayCache[(int)type]?.EnName ?? type.ToString();

        // --- ⬅️ 역방향 확장 메서드 3종 세트 (문자열 -> Enum) ★ ---

        // 1. 코드로 찾기 (예: "01" -> TargetMuscle.Chest)
        public static TargetMuscle ToTargetMuscleFromCode(this string code)
        {
            if (code == null) return default;
            return _codeCache.TryGetValue(code, out var type) ? type : default;
        }

        // 2. 한국명으로 찾기 (예: "가슴" -> TargetMuscle.Chest)
        public static TargetMuscle ToTargetMuscleFromKoName(this string koName)
        {
            if (koName == null) return default;
            return _koNameCache.TryGetValue(koName, out var type) ? type : default;
        }

        // 3. 영어명으로 찾기 (예: "CHEST" -> TargetMuscle.Chest / 대소문자 구분 없음)
        public static TargetMuscle ToTargetMuscleFromEnName(this string enName)
        {
            if (enName == null) return default;
            return _enNameCache.TryGetValue(enName, out var type) ? type : default;
        }
    }
}