using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ============================================================================
// СИСТЕМА ПОТРЕБНОСТЕЙ RESIDENCE (По типу Anno 1800)
// ============================================================================
//
// КАК РАБОТАЕТ:
// 1. Каждый дом (Residence) имеет список потребностей (basicNeeds)
// 2. Каждая потребность (Need) настраивается в Inspector:
//    - Сколько ресурса нужно в минуту
//    - Сколько жителей дает при удовлетворении
//    - Сколько счастья дает/забирает
//    - Сколько налога дает
// 3. Каждые X секунд (consumptionIntervalSeconds) дом проверяет потребности
// 4. Если потребность удовлетворена → применяются БОНУСЫ
//    Если НЕ удовлетворена → применяются ШТРАФЫ
//
// ============================================================================
// ПРИМЕР НАСТРОЙКИ В UNITY:
// ============================================================================
//
// --- Residence (компонент на доме) ---
// populationTier = Farmers
// housingCapacity = 10                    // Максимум 10 жителей
// consumptionIntervalSeconds = 10         // Проверяем каждые 10 секунд
// baseTaxAmount = 1.0                     // Базовый налог (всегда платится)
//
// basicNeeds (список):
//   [0] Рыба (базовая еда):
//       resourceType = Fish
//       amountPerMinute = 0.1            // 10 домов = 1 рыба/мин
//       populationBonus = 5              // +5 жителей
//       happinessBonus = 0.5             // +0.5 счастья
//       taxBonusPerCycle = 0.5           // +0.5 золота
//       happinessPenalty = -1.0          // -1.0 счастья (голод критичен!)
//
//   [1] Одежда (комфорт):
//       resourceType = Clothes
//       amountPerMinute = 0.05           // 20 домов = 1 одежда/мин
//       populationBonus = 3              // +3 жителя
//       happinessBonus = 0.3             // +0.3 счастья
//       taxBonusPerCycle = 1.0           // +1.0 золота
//       happinessPenalty = -0.5          // -0.5 счастья
//
//   [2] Пиво (роскошь):
//       resourceType = Beer
//       amountPerMinute = 0.033          // 30 домов = 1 пиво/мин
//       populationBonus = 2              // +2 жителя
//       happinessBonus = 0.2             // +0.2 счастья
//       taxBonusPerCycle = 2.0           // +2.0 золота
//       happinessPenalty = -0.2          // -0.2 счастья
//
// --- РЕЗУЛЬТАТЫ (при всех потребностях удовлетворены) ---
// Жители: 5 + 3 + 2 = 10 (лимит housingCapacity)
// Счастье за цикл: +0.5 + 0.3 + 0.2 = +1.0
// Налог за цикл: 1.0 (базовый) + 0.5 + 1.0 + 2.0 = 4.5 золота
//
// --- РЕЗУЛЬТАТЫ (только Рыба, без Одежды и Пива) ---
// Жители: 5 (только бонус от Рыбы)
// Счастье за цикл: +0.5 (Рыба) -0.5 (нет Одежды) -0.2 (нет Пива) = -0.2
// Налог за цикл: 1.0 (базовый) + 0.5 (Рыба) = 1.5 золота
//
// ============================================================================
// ФОРМУЛА: "X домов потребляют Y ресурса в минуту"
// ============================================================================
//
// Хотите: "10 домов потребляют 1 Рыбу в минуту"
// Решение: amountPerMinute = 1.0 ÷ 10 = 0.1 (на ОДИН дом)
//
// Хотите: "20 домов потребляют 1 Одежду в минуту"
// Решение: amountPerMinute = 1.0 ÷ 20 = 0.05 (на ОДИН дом)
//
// Хотите: "30 домов потребляют 1 Пиво в минуту"
// Решение: amountPerMinute = 1.0 ÷ 30 = 0.033 (на ОДИН дом)
//
// ============================================================================
// ПОНИМАНИЕ consumptionIntervalSeconds vs amountPerMinute
// ============================================================================
//
// consumptionIntervalSeconds - КАК ЧАСТО проверяем (10 сек = 6 раз в минуту)
// amountPerMinute - СКОЛЬКО ВСЕГО нужно за 1 минуту
//
// При каждой проверке списывается:
//   amountToConsume = amountPerMinute × (consumptionIntervalSeconds ÷ 60)
//
// Пример (consumptionIntervalSeconds = 10, amountPerMinute = 0.1):
//   Каждые 10 секунд: 0.1 × (10 ÷ 60) = 0.1 × 0.1667 = 0.01667 рыбы
//   За минуту (6 проверок): 0.01667 × 6 = 0.1 рыбы ✓
//
// Рекомендация: Оставить consumptionIntervalSeconds = 10 (удобный баланс)
//              Настраивать только amountPerMinute
//
// ============================================================================

/// <summary>
/// (Новый struct) Временный "контейнер" для результата проверки 1 потребности.
/// </summary>
public struct NeedResult
{
    public Need need;  // Ссылка на "настройки" потребности (откуда брать бонусы)
    public bool isMet; // Результат (Да/Нет)
}


[RequireComponent(typeof(BuildingIdentity))]
public class Residence : MonoBehaviour
{
    [Header("=== Уровень Дома ===")]
    [Tooltip("Уровень населения (Farmers/Craftsmen/Artisans)")]
    public PopulationTier populationTier = PopulationTier.Farmers;

    [Tooltip("МАКСИМАЛЬНАЯ вместимость дома\n\n" +
             "ВАЖНО: Фактическое количество жителей = сумма populationBonus из УДОВЛЕТВОРЕННЫХ потребностей\n" +
             "Это поле ограничивает максимум (нельзя превысить этот лимит)")]
    public int housingCapacity = 10;

    [Header("=== Система Потребностей (Anno 1800) ===")]
    [Tooltip("Список потребностей дома (Рыба, Одежда, Пиво и т.д.)\n\n" +
             "Каждая потребность настраивается отдельно:\n" +
             "  • Сколько ресурса нужно в минуту\n" +
             "  • Сколько жителей дает (populationBonus)\n" +
             "  • Бонусы к счастью и налогам\n\n" +
             "См. примеры в Need.cs и комментарии выше")]
    public List<Need> basicNeeds;

    [Tooltip("Как часто проверяем потребности (в секундах)\n\n" +
             "РЕКОМЕНДУЕТСЯ: 10 секунд (6 проверок в минуту)\n" +
             "Уменьшать не нужно! Настраивайте amountPerMinute в каждой Need")]
    public float consumptionIntervalSeconds = 10f;

    [Header("=== Налоги ===")]
    [Tooltip("БАЗОВЫЙ налог (платится ВСЕГДА, даже при голоде)\n\n" +
             "Дополнительные налоги настраиваются в каждой потребности (Need.taxBonusPerCycle)\n\n" +
             "Итоговый налог = baseTaxAmount + сумма taxBonusPerCycle из удовлетворенных потребностей")]
    public float baseTaxAmount = 1f;
    
    // (Убраны 'happinessGain' и 'happinessPenalty', т.к. они теперь в Need.cs)

    // --- Ссылки на системы ---
    private BuildingIdentity _identity;
    private AuraManager _auraManager;
    private ResourceManager _resourceManager;
    private TaxManager _taxManager;
    private HappinessManager _happinessManager;
    private PopulationManager _populationManager;

    // Текущий налог (для плавного начисления через TaxManager)
    private float _currentTax;

    // Текущее количество жителей (рассчитывается на основе удовлетворения потребностей)
    private int _currentResidents = 0;

    // Процент удовлетворения потребностей (0.0 - 1.0)
    private float _needsSatisfactionRate = 1.0f;

    private void Start()
    {
        // (Получаем ссылки... без изменений)
        _identity = GetComponent<BuildingIdentity>();
        _auraManager = AuraManager.Instance;
        _resourceManager = ResourceManager.Instance;
        _taxManager = TaxManager.Instance;
        _happinessManager = HappinessManager.Instance;
        _populationManager = PopulationManager.Instance;

        if (_auraManager == null || _resourceManager == null || _taxManager == null || _happinessManager == null || _populationManager == null)
        {
            Debug.LogError($"[Residence] на {gameObject.name} не смог найти один из 'мозгов' (Managers). Экономика сломана.");
            this.enabled = false;
            return;
        }

        // Регистрируем жилье в PopulationManager
        if (!_identity.isBlueprint)
        {
            _populationManager.AddHousingCapacity(populationTier, housingCapacity);
            Debug.Log($"[Residence] {gameObject.name} зарегистрирован в PopulationManager: {populationTier}, вместимость: {housingCapacity}");
        }

        StartCoroutine(ConsumeNeedsCoroutine());
    }

    private void OnDestroy()
    {
        // Снимаем регистрацию жилья при уничтожении
        if (_populationManager != null && !_identity.isBlueprint)
        {
            _populationManager.RemoveHousingCapacity(populationTier, housingCapacity);
            Debug.Log($"[Residence] {gameObject.name} снят с регистрации из PopulationManager");
        }
    }

    /// <summary>
    /// Главный цикл жизни дома.
    /// </summary>
    private IEnumerator ConsumeNeedsCoroutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(consumptionIntervalSeconds);

            if (_identity.isBlueprint)
            {
                continue; // "Проекты" не потребляют
            }

            // --- ЛОГИКА 3.0: Гранулярная проверка ---
            
            // 1. Проверяем доступ к Ауре (Сервисы)
            bool hasMarket = CheckServiceNeeds();
            
            // 2. Проверяем и "съедаем" Ресурсы (если есть доступ к Рынку)
            List<NeedResult> resourceResults;
            if (hasMarket)
            {
                // Если рынок есть - пытаемся "купить" (списать)
                resourceResults = CheckAndConsumeResourceNeeds();
            }
            else
            {
                // Если рынка нет - "проваливаем" ВСЕ потребности
                resourceResults = FailAllResourceNeeds();
            }

            // 3. Обрабатываем ВСЕ результаты (Счастье и Налоги)
            ProcessResults(resourceResults);
            
            // --- КОНЕЦ ЛОГИКИ 3.0 ---
        }
    }

    /// <summary>
    /// (Шаг А) Проверяет доступ к "сервисным" зданиям (Рынок).
    /// </summary>
    private bool CheckServiceNeeds()
    {
        // (Пока проверяем только Рынок, как и раньше)
        bool hasMarket = _auraManager.IsPositionInAura(transform.position, AuraType.Market);
        
        if (!hasMarket)
        {
            Debug.Log($"[Residence] {gameObject.name} не имеет доступа к Рынку!"); 
        }
        return hasMarket;
    }

    /// <summary>
    /// (Шаг Б - УСПЕХ) Пытается "купить" (списать) ресурсы и возвращает отчет.
    /// </summary>
    private List<NeedResult> CheckAndConsumeResourceNeeds()
    {
        float intervalAsFractionOfMinute = consumptionIntervalSeconds / 60f;
        var results = new List<NeedResult>();

        // --- ПЕРЕДЕЛКА: "По-предметная" проверка ---
        results.Clear();
        foreach (var need in basicNeeds)
        {
            float amountToConsume = need.amountPerMinute * intervalAsFractionOfMinute;

            // Пытаемся "купить" (списать) ЭТОТ ОДИН предмет
            bool success = _resourceManager.TakeFromStorage(need.resourceType, amountToConsume) > 0;

            results.Add(new NeedResult { need = need, isMet = success });

            if (!success)
                Debug.Log($"[Residence] {gameObject.name} не хватило {need.resourceType}!");
        }

        return results;
    }

    /// <summary>
    /// (Шаг Б - ПРОВАЛ) "Проваливает" все потребности (т.к. нет рынка).
    /// </summary>
    private List<NeedResult> FailAllResourceNeeds()
    {
        var results = new List<NeedResult>();
        foreach (var need in basicNeeds)
        {
            results.Add(new NeedResult { need = need, isMet = false });
        }
        return results;
    }

    /// <summary>
    /// (Шаг В) Обрабатывает гранулярные результаты.
    /// НОВАЯ СИСТЕМА (Anno 1800): каждая потребность дает свой бонус к населению, счастью, налогам
    /// </summary>
    private void ProcessResults(List<NeedResult> resourceResults)
    {
        float totalHappinessChange = 0;
        float totalTax = baseTaxAmount; // Начинаем с "базового" налога
        int totalPopulation = 0; // НОВОЕ: суммируем жителей из каждой потребности

        // Подсчитываем процент удовлетворения потребностей (для статистики)
        int totalNeeds = resourceResults.Count;
        int satisfiedNeeds = 0;

        foreach (var result in resourceResults)
        {
            if (result.isMet)
            {
                satisfiedNeeds++;
                // ========== УСПЕХ: Потребность УДОВЛЕТВОРЕНА ==========
                // (напр. "Рыба" куплена, "Одежда" в наличии)

                totalPopulation += result.need.populationBonus;  // +5 жителей за Рыбу, +3 за Одежду и т.д.
                totalHappinessChange += result.need.happinessBonus; // +0.5 счастья
                totalTax += result.need.taxBonusPerCycle; // +0.5 золота (довольные люди платят больше)
            }
            else
            {
                // ========== ПРОВАЛ: Потребность НЕ УДОВЛЕТВОРЕНА ==========
                // (напр. "Нет Рыбы" или "Нет доступа к Рынку")

                // НЕ добавляем жителей (populationBonus не применяется)
                totalHappinessChange += result.need.happinessPenalty; // -1.0 счастья (голод!)
                // Налог не увеличивается (только базовый)
            }
        }

        // Рассчитываем процент удовлетворения потребностей (для отладки/UI)
        _needsSatisfactionRate = (totalNeeds > 0) ? ((float)satisfiedNeeds / (float)totalNeeds) : 1.0f;

        // НОВАЯ СИСТЕМА: жители = сумма populationBonus из УДОВЛЕТВОРЕННЫХ потребностей
        // Пример: Рыба (+5) + Одежда (+3) = 8 жителей
        //         Только Рыба (+5) = 5 жителей
        //         Ничего не удовлетворено = 0 жителей
        _currentResidents = Mathf.Min(totalPopulation, housingCapacity); // Не превышаем лимит дома

        // "Отправляем" итоговые цифры в менеджеры
        _happinessManager.AddHappiness(totalHappinessChange);
        _currentTax = totalTax; // TaxManager заберет плавно

        // Обновляем PopulationManager о текущем количестве жителей
        Debug.Log($"[Residence] {gameObject.name}: " +
                  $"Потребности: {satisfiedNeeds}/{totalNeeds} ({_needsSatisfactionRate * 100f:F1}%), " +
                  $"Жителей: {_currentResidents}/{housingCapacity}, " +
                  $"Счастье: {totalHappinessChange:+0.0;-0.0}, " +
                  $"Налог: {totalTax:F2}");
    }

    /// <summary>
    /// Возвращает текущий налог этого дома (для TaxManager).
    /// </summary>
    public float GetCurrentTax()
    {
        // Если это чертеж, налог = 0
        if (_identity != null && _identity.isBlueprint)
        {
            return 0;
        }

        return _currentTax;
    }
}