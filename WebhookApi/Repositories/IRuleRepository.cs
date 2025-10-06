namespace RedwoodIloilo.Common.Entities;
public interface IRuleRepository
{
    /// <summary>
    /// Returns a list of rules relevant to a guest's question.
    /// </summary>
    Task<List<Rule>> GetRelevantRulesAsync(string question);

    /// <summary>
    /// Returns all rules (optional utility).
    /// </summary>
    Task<List<Rule>> GetAllAsync();

    /// <summary>
    /// Returns a single rule by ID.
    /// </summary>
    Task<Rule?> GetByIdAsync(int id);

    /// <summary>
    /// Adds a new rule to the database.
    /// </summary>
    Task AddAsync(Rule rule);

    /// <summary>
    /// Persists changes to the database.
    /// </summary>
    Task SaveChangesAsync();
}
