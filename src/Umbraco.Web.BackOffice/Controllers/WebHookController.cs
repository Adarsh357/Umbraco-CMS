﻿using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Mapping;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Controllers;
using Umbraco.Cms.Web.Common.Models;

namespace Umbraco.Cms.Web.BackOffice.Controllers;

public class WebHookController : UmbracoApiController
{
    private readonly IWebHookService _webHookService;
    private readonly IUmbracoMapper _umbracoMapper;

    public WebHookController(IWebHookService webHookService, IUmbracoMapper umbracoMapper)
    {
        _webHookService = webHookService;
        _umbracoMapper = umbracoMapper;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        IEnumerable<Webhook> webhooks = await _webHookService.GetAllAsync();

        List<WebhookViewModel> webhookViewModels = _umbracoMapper.MapEnumerable<Webhook, WebhookViewModel>(webhooks);

        return Ok(webhookViewModels);
    }

    [HttpPost]
    public async Task<IActionResult> Create(WebhookViewModel webhookViewModel)
    {
        Webhook webhook = _umbracoMapper.Map<Webhook>(webhookViewModel)!;
        await _webHookService.CreateAsync(webhook);

        return Ok();
    }

    [HttpGet]
    public async Task<IActionResult> GetByKey(Guid key)
    {
        Webhook? webhook = await _webHookService.GetAsync(key);

        return webhook is null ? NotFound() : Ok(webhook);
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(Guid key)
    {
        await _webHookService.DeleteAsync(key);

        return Ok();
    }

    // TODO: This should probably be handled by the NewtonsoftJsonOutputFormatter instead
    [HttpGet]
    public async Task<IActionResult> GetEvents() =>
        Ok(Enum.GetValues(typeof(WebhookEvent)).Cast<WebhookEvent>().Select(x => x.ToString()));
}
