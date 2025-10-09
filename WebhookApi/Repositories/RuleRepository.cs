using Microsoft.EntityFrameworkCore;
using WebhookApi.Data;

namespace RedwoodIloilo.Common.Entities;
public class RuleRepository : IRuleRepository
{
    private readonly AppDbContext _context;

    public RuleRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<Rule>> GetAllAsync()
    {
        return await _context.Rules
            .Include(r => r.RuleCategory)
            .ToListAsync();
    }

    public async Task<Rule?> GetByIdAsync(int id)
    {
        return await _context.Rules
            .Include(r => r.RuleCategory)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    public async Task AddAsync(Rule rule)
    {
        await _context.Rules.AddAsync(rule);
    }

    public async Task SaveChangesAsync()
    {
        await _context.SaveChangesAsync();
    }

    public async Task<List<Rule>> GetRelevantRulesAsync(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new List<Rule>();
            
        question = question.ToLower();

        var categoryKeywords = new Dictionary<string, string[]>
        {
            { "Suitability", new[] 
                { "children", "kids", "baby", "infant", "child", "family", "elderly", "wheelchair", "accessible", "handicap" } 
            },

            { "Check-in/Check-out", new[] 
                { "check-in", "check in", "check-out", "check out", "arrival", "departure", "early check-in", "late check-out", "time", "when can we arrive", "when can we leave" } 
            },

            { "Electricity & Early/Late Stay Policy", new[] 
                { "electricity", "power", "lights", "energy", "aircon", "air conditioning", "fan", "appliance", "early check-in", "late check-out", "stay longer" } 
            },

            { "Smoking", new[] 
                { "smoking", "smoke", "cigarette", "vape", "e-cigarette", "ashtray", "smoke outside", "smoking area" } 
            },

            { "Pets", new[] 
                { "pet", "dog", "cat", "animal", "bring my pet", "pet friendly", "pets allowed" } 
            },

            { "Quiet Hours", new[] 
                { "quiet", "noise", "music", "loud", "party noise", "neighbors", "silence", "sound", "volume" } 
            },

            { "Parties and Events", new[] 
                { "party", "event", "celebration", "birthday", "gathering", "get together", "wedding", "reception" } 
            },

            { "Maximum Occupancy", new[] 
                { "maximum guests", "how many people", "occupancy", "extra guest", "more guests", "additional person", "visitor staying", "guest limit" } 
            },

            { "Visitors", new[] 
                { "visitor", "guest", "friend", "family visit", "outside guest", "invite someone", "day visitor" } 
            },

            { "Damages", new[] 
                { "damage", "broken", "repair", "lost", "accident", "deposit", "security deposit", "responsible", "compensation" } 
            },

            { "Kitchen Use", new[] 
                { "kitchen", "cook", "cooking", "stove", "microwave", "fridge", "utensil", "pan", "food", "meal", "oven", "dish" } 
            },

            { "Garbage and Recycling", new[] 
                { "trash", "garbage", "waste", "rubbish", "bin", "recycle", "disposal", "compost", "throw", "clean up" } 
            },

            { "Amenities", new[] 
                { "laundry", "washing machine", "washing", "machine", "dryer", "parking", "pool", "gym", "wifi", "internet", "tv", "kettle", "iron", "hair dryer" } 
            },

            { "Security and Safety", new[] 
                { "lock", "key", "door", "security", "safety", "camera", "cctv", "alarm", "safe", "fire", "emergency", "exit", "first aid" } 
            },

            { "Lost Items", new[] 
                { "lost", "left behind", "forgot", "missing", "found", "item", "belonging", "retrieve", "pickup" } 
            },

            { "Wi-Fi", new[] 
                { "wifi", "wi-fi", "internet", "password", "connect", "network", "slow internet", "connection", "router" } 
            },

            { "Heating/Cooling", new[] 
                { "aircon", "air conditioning", "heater", "heat", "cooling", "temperature", "thermostat", "fan", "warm", "cold" } 
            },

            { "Communication", new[] 
                { "message", "contact", "call", "sms", "whatsapp", "text", "reach", "respond", "reply", "communication", "host", "manager" } 
            }
        };


        string? matchedCategory = categoryKeywords
            .FirstOrDefault(kvp => kvp.Value.Any(keyword => question.Contains(keyword)))
            .Key;

        IQueryable<Rule> query = _context.Rules.Include(r => r.RuleCategory);

        if (!string.IsNullOrEmpty(matchedCategory))
            query = query.Where(r => r.RuleCategory.Name == matchedCategory);
        else
            query = query.Take(5); // fallback: limit

        return await query.ToListAsync();
    }
}
