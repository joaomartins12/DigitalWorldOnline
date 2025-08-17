
using DigitalWorldOnline.Commons.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DigitalWorldOnline.Infraestructure
{
    public partial class DatabaseContext : DbContext
    {
        private const string DatabaseConnectionString = "Database:Connection";
        private readonly IConfiguration _configuration;
        private readonly bool _cliInitialization;

        public DatabaseContext()
        {
            _cliInitialization = true;
        }

        public DatabaseContext(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) //DESKTOP-EH8O5NE
        {
            //if (_cliInitialization)
            optionsBuilder.UseSqlServer("Server=26.249.80.49\\MSSQL;Database=dmo;User Id=sa;Password=123456;TrustServerCertificate=True;", sqlServerOptions =>
            {
                sqlServerOptions.EnableRetryOnFailure(
                    maxRetryCount: 5, // Número máximo de tentativas
                    maxRetryDelay: TimeSpan.FromSeconds(3), // Tempo máximo de espera entre as tentativas
                    errorNumbersToAdd: null // Lista de códigos de erro adicionais para considerar como transitórios
                );
            });
            /*var configurationDatabaseKey = _configuration[Constants.Configuration.DatabaseKey];
            //Set a system environment variable with key DMO_ConnectionStrings:Digimon
            var systemEnvironmentKey = Environment.GetEnvironmentVariable($"{Constants.Configuration.EnvironmentPrefix}{Constants.Configuration.DatabaseKey}", EnvironmentVariableTarget.Machine);
            optionsBuilder.UseSqlServer(systemEnvironmentKey ?? configurationDatabaseKey);*/


            //options.LogTo(Console.WriteLine, LogLevel.Debug);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            SharedEntityConfiguration(modelBuilder);
            AccountEntityConfiguration(modelBuilder);
            AssetsEntityConfiguration(modelBuilder);
            CharacterEntityConfiguration(modelBuilder);
            ConfigEntityConfiguration(modelBuilder);
            DigimonEntityConfiguration(modelBuilder);
            EventEntityConfiguration(modelBuilder);
            SecurityEntityConfiguration(modelBuilder);
            ShopEntityConfiguration(modelBuilder);
            MechanicsEntityConfiguration(modelBuilder);
            RoutineEntityConfiguration(modelBuilder);
            ArenaEntityConfiguration(modelBuilder);
        }
    }
}