using AutoMapper;
using Durable.Api.Functions;
using Durable.Functions.Models;
using Durable.Functions.Validators;
using Durable.Service.Models;
using Durable.Services.Interfaces;
using Durable.Utilities;
using FluentValidation.Results;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Models;
using System.Net;

namespace Durable.Functions
{
    public class EnvImpactDurableFunction : BaseFunction
    {
        private readonly ILogger<EnvImpactDurableFunction> _logger;
        private readonly IMapper _mapper;
        private readonly IEnvImpactReportService _dataExportService;
        private readonly IServiceProviderValidatorFactory _validatorFactory;
        private readonly IMemoryCache _memoryCache;

        public EnvImpactDurableFunction(
            ILogger<EnvImpactDurableFunction> logger,
            IMapper mapper,
            IEnvImpactReportService dataExportService,
            IServiceProviderValidatorFactory validatorFactory,
            IMemoryCache memoryCache)
        {
            _logger = logger;
            _dataExportService = dataExportService;
            _validatorFactory = validatorFactory;
            _memoryCache = memoryCache;
            _mapper = mapper;
        }

        [Function(nameof(EnvImpactDurableFunction))]
        public async Task<string> RunOrchestrator(
             [OrchestrationTrigger] TaskOrchestrationContext context,
             GetBaseRequest request)
        {
            _logger.LogInformation($"Running orcestration for {request.ReportName} report.");
            string outputs = await context.CallActivityAsync<string>(nameof(GetReportsRun), request);
            return outputs;
        }

        [Function(nameof(GetReportsRun))]
        public async Task<string> GetReportsRun(
            [ActivityTrigger] GetBaseRequest request)
        {
            _logger.LogInformation($"GetReportsRun for report {request.ReportName}.");
            string cacheKey = string.Concat("filename", request.ReportName, request.Name, request.Region, request.Percentage);
            string filename = (string)ServiceUtilities.GetCachedObject(_memoryCache, cacheKey);
            if (!filename.IsNullOrEmpty())
            {
                return filename;
            }
            string outputs = "failed";
            outputs = await _dataExportService.GetReportAsync(_mapper.Map<ReportBaseModel>(request));

            ServiceUtilities.SetCachedObject(_memoryCache, cacheKey, outputs);

            return outputs;
        }

        [Function("GetSpeciesEnvImpact")]
        [OpenApiOperation(operationId: nameof(GetSpeciesEnvImpact), tags: new[] { nameof(GetSpeciesEnvImpact) })]
        [OpenApiParameter(name: "name", In = ParameterLocation.Query, Required = true, Type = typeof(string), Description = "The Specie parameter to filter on")]
        [OpenApiParameter(name: "region", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "The Region parameter to filter on")]
        [OpenApiParameter(name: "percentage", In = ParameterLocation.Query, Required = false, Type = typeof(int), Description = "The Percentage parameter to filter on")]
        public async Task<HttpResponseData> GetSpeciesEnvImpact(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "species")] HttpRequestData req,
            [DurableClient] DurableTaskClient client,
            FunctionContext executionContext,
            IDictionary<string, string> query)
        {
            _logger.LogInformation($"GetSpeciesEnvImpact with query: {query?.Keys}");

            (GetBaseRequest reportRequest, ValidationResult validationResult) request = await GetBaseRequestAsync(req, query, "Species");

            if (!request.validationResult.IsValid)
            {
                return await HandleValidationResponse(req, request.validationResult);
            }


            // Function input comes from the request content.
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(EnvImpactDurableFunction), request.reportRequest);

            _logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);

            // Returns an HTTP 202 response with an instance management payload.
            // See https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-http-api#start-orchestration
            return await client.CreateCheckStatusResponseAsync(req, instanceId, HttpStatusCode.Accepted);
        }

        [Function("GetResourcesEnvImpact")]
        [OpenApiOperation(operationId: nameof(GetResourcesEnvImpact), tags: new[] { nameof(GetResourcesEnvImpact) })]
        [OpenApiParameter(name: "name", In = ParameterLocation.Query, Required = true, Type = typeof(string), Description = "The Resource parameter to filter on")]
        [OpenApiParameter(name: "region", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "The Region parameter to filter on")]
        [OpenApiParameter(name: "percentage", In = ParameterLocation.Query, Required = false, Type = typeof(int), Description = "The Percentage parameter to filter on")]
        public async Task<HttpResponseData> GetResourcesEnvImpact(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "resources")] HttpRequestData req,
            [DurableClient] DurableTaskClient client,
            FunctionContext executionContext,
            IDictionary<string, string> query)
        {
            _logger.LogInformation($"GetResourcesEnvImpact with query: {query?.Keys}");
            (GetBaseRequest reportRequest, ValidationResult validationResult) request = await GetBaseRequestAsync(req, query, "Resources");

            if (!request.validationResult.IsValid)
            {
                return await HandleValidationResponse(req, request.validationResult);
            }

            // Function input comes from the request content.
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
                nameof(EnvImpactDurableFunction), request.reportRequest);

            _logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);

            // Returns an HTTP 202 response with an instance management payload.
            // See https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-http-api#start-orchestration
            return await client.CreateCheckStatusResponseAsync(req, instanceId, HttpStatusCode.Accepted);
        }

        private async Task<(GetBaseRequest reportRequest, ValidationResult validationResult)> GetBaseRequestAsync(HttpRequestData req, IDictionary<string, string> query, string reportName)
        {
            query.TryGetValue("name", out string name);
            query.TryGetValue("region", out string? region);
            query.TryGetValue("percentage", out string? percentage);

            GetBaseRequest result = new GetBaseRequest()
            {
                Name = name,
                Region = region,
                Percentage = percentage,
                ReportName = reportName,
            };

            GetBaseRequestValidator validator = new GetBaseRequestValidator();
            ValidationResult validationResult = validator.Validate(result);

            return (result, validationResult);
        }
    }
}
