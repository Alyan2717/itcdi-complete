using Microsoft.Extensions.Logging;
using Quartz;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ITCDI.Infrastructure.Jobs
{
    [DisallowConcurrentExecution]
    public class IntegrationJob : IJob
    {
        private readonly ILogger<IntegrationJob> _logger;

        public IntegrationJob(ILogger<IntegrationJob> logger)
        {
            _logger = logger;
        }

        public Task Execute(IJobExecutionContext context)
        {
            _logger.LogInformation("Scheduled integration job triggered at {Time}", DateTime.UtcNow);

            // TODO: implement scheduled source fetching
            // This will call IntegrationOrchestrator for each
            // configured source in appsettings.json

            return Task.CompletedTask;
        }
    }
}
