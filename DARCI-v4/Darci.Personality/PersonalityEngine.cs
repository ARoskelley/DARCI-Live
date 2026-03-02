using Dapper;
using Darci.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Darci.Personality;

public class PersonalityEngine : IPersonalityEngine
{
    private readonly ILogger<PersonalityEngine> _logger;
    private readonly string _connectionString;
    
    private PersonalityTraits _traits = new();
    
    public PersonalityEngine(ILogger<PersonalityEngine> logger, string connectionString)
    {
        _logger = logger;
        _connectionString = connectionString;
        InitializeDatabase();
    }
    
    public async Task<PersonalityTraits> LoadTraits()
    {
        using var conn = new SqliteConnection(_connectionString);
        
        var record = await conn.QueryFirstOrDefaultAsync<TraitsRecord>(
            "SELECT * FROM PersonalityTraits WHERE Id = 1");
        
        if (record == null)
        {
            // Initialize with defaults
            _traits = new PersonalityTraits();
            await SaveTraits(_traits);
            return _traits;
        }
        
        _traits = new PersonalityTraits
        {
            Warmth = record.Warmth,
            HumorAffinity = record.HumorAffinity,
            Reflectiveness = record.Reflectiveness,
            Confidence = record.Confidence,
            Trust = record.Trust,
            Curiosity = record.Curiosity,
            BaselineEnergy = record.BaselineEnergy
        };
        
        _logger.LogInformation("Loaded personality traits - Warmth: {Warmth:P0}, Trust: {Trust:P0}", 
            _traits.Warmth, _traits.Trust);
        
        return _traits;
    }
    
    public async Task<PersonalityState?> LoadState()
    {
        using var conn = new SqliteConnection(_connectionString);
        
        var record = await conn.QueryFirstOrDefaultAsync<StateRecord>(
            "SELECT * FROM PersonalityState WHERE Id = 1");
        
        if (record == null) return null;
        
        return new PersonalityState
        {
            Mood = Enum.Parse<Mood>(record.Mood),
            MoodIntensity = record.MoodIntensity,
            Energy = record.Energy,
            Focus = record.Focus
        };
    }
    
    public async Task SaveState(PersonalityState state)
    {
        using var conn = new SqliteConnection(_connectionString);
        
        await conn.ExecuteAsync(@"
            INSERT OR REPLACE INTO PersonalityState (Id, Mood, MoodIntensity, Energy, Focus, UpdatedAt)
            VALUES (1, @Mood, @MoodIntensity, @Energy, @Focus, @UpdatedAt)",
            new
            {
                Mood = state.Mood.ToString(),
                state.MoodIntensity,
                state.Energy,
                state.Focus,
                UpdatedAt = DateTime.UtcNow.ToString("o")
            });
    }
    
    public async Task NudgeTrait(TraitType trait, float amount)
    {
        // Apply the nudge with bounds checking
        switch (trait)
        {
            case TraitType.Warmth:
                _traits.Warmth = Clamp(_traits.Warmth + amount, 0.3f, 0.95f);
                break;
            case TraitType.HumorAffinity:
                _traits.HumorAffinity = Clamp(_traits.HumorAffinity + amount, 0.1f, 0.7f);
                break;
            case TraitType.Reflectiveness:
                _traits.Reflectiveness = Clamp(_traits.Reflectiveness + amount, 0.3f, 0.8f);
                break;
            case TraitType.Confidence:
                _traits.Confidence = Clamp(_traits.Confidence + amount, 0.5f, 0.9f);
                break;
            case TraitType.Trust:
                _traits.Trust = Clamp(_traits.Trust + amount, 0.2f, 0.95f);
                break;
            case TraitType.Curiosity:
                _traits.Curiosity = Clamp(_traits.Curiosity + amount, 0.4f, 0.9f);
                break;
        }
        
        // Persist periodically (not every nudge)
        if (Random.Shared.NextDouble() < 0.1)
        {
            await SaveTraits(_traits);
        }
    }
    
    private async Task SaveTraits(PersonalityTraits traits)
    {
        using var conn = new SqliteConnection(_connectionString);
        
        await conn.ExecuteAsync(@"
            INSERT OR REPLACE INTO PersonalityTraits 
            (Id, Warmth, HumorAffinity, Reflectiveness, Confidence, Trust, Curiosity, BaselineEnergy, UpdatedAt)
            VALUES (1, @Warmth, @HumorAffinity, @Reflectiveness, @Confidence, @Trust, @Curiosity, @BaselineEnergy, @UpdatedAt)",
            new
            {
                traits.Warmth,
                traits.HumorAffinity,
                traits.Reflectiveness,
                traits.Confidence,
                traits.Trust,
                traits.Curiosity,
                traits.BaselineEnergy,
                UpdatedAt = DateTime.UtcNow.ToString("o")
            });
        
        _logger.LogDebug("Saved personality traits");
    }
    
    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS PersonalityTraits (
                Id INTEGER PRIMARY KEY,
                Warmth REAL NOT NULL,
                HumorAffinity REAL NOT NULL,
                Reflectiveness REAL NOT NULL,
                Confidence REAL NOT NULL,
                Trust REAL NOT NULL,
                Curiosity REAL NOT NULL,
                BaselineEnergy REAL NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            
            CREATE TABLE IF NOT EXISTS PersonalityState (
                Id INTEGER PRIMARY KEY,
                Mood TEXT NOT NULL,
                MoodIntensity REAL NOT NULL,
                Energy REAL NOT NULL,
                Focus REAL NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
        ");
    }
    
    private float Clamp(float value, float min, float max)
        => Math.Max(min, Math.Min(max, value));
    
    private class TraitsRecord
    {
        public float Warmth { get; set; }
        public float HumorAffinity { get; set; }
        public float Reflectiveness { get; set; }
        public float Confidence { get; set; }
        public float Trust { get; set; }
        public float Curiosity { get; set; }
        public float BaselineEnergy { get; set; }
    }
    
    private class StateRecord
    {
        public string Mood { get; set; } = "";
        public float MoodIntensity { get; set; }
        public float Energy { get; set; }
        public float Focus { get; set; }
    }
}
