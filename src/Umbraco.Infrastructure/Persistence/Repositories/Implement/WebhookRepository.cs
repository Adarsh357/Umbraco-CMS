﻿using Microsoft.Extensions.Logging;
using NPoco;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Persistence.Querying;
using Umbraco.Cms.Core.Persistence.Repositories;
using Umbraco.Cms.Infrastructure.Persistence.Dtos;
using Umbraco.Cms.Infrastructure.Persistence.Factories;
using Umbraco.Cms.Infrastructure.Persistence.Querying;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace Umbraco.Cms.Infrastructure.Persistence.Repositories.Implement;

public class WebhookRepository : EntityRepositoryBase<int, Webhook>, IWebhookRepository
{
    public WebhookRepository(IScopeAccessor scopeAccessor, AppCaches appCaches, ILogger<EntityRepositoryBase<int, Webhook>> logger) : base(scopeAccessor, appCaches, logger)
    {
    }

    protected override Webhook? PerformGet(int id)
    {
        Sql<ISqlContext> sql = GetBaseQuery(false);
        sql.Where(GetBaseWhereClause(), new { Id = id });

        WebhookDto? dto = Database.First<WebhookDto>(sql);
        return dto == null
            ? null
            : DtoToEntity(dto);
    }

    protected override IEnumerable<Webhook> PerformGetAll(params int[]? ids)
    {
        Sql<ISqlContext> sql = GetBaseQuery(false);

        List<WebhookDto>? dtos = Database.Fetch<WebhookDto>(sql);

        return dtos.Select(DtoToEntity);
    }

    protected override IEnumerable<Webhook> PerformGetByQuery(IQuery<Webhook> query)
    {
        Sql<ISqlContext> sqlClause = GetBaseQuery(false);
        var translator = new SqlTranslator<Webhook>(sqlClause, query);
        Sql<ISqlContext> sql = translator.Translate();

        List<WebhookDto>? dtos = Database.Fetch<WebhookDto>(sql);

        return dtos.Select(DtoToEntity);
    }

    protected override void PersistNewItem(Webhook entity)
    {
        entity.AddingEntity();

        WebhookDto dto = WebhookFactory.BuildDto(entity);

        var id = Convert.ToInt32(Database.Insert(dto));
        entity.Id = id;

        entity.ResetDirtyProperties();
    }

    protected override void PersistUpdatedItem(Webhook entity)
    {
        entity.UpdatingEntity();

        WebhookDto dto = WebhookFactory.BuildDto(entity);
        Database.Update(dto);

        entity.ResetDirtyProperties();
    }

    protected override Sql<ISqlContext> GetBaseQuery(bool isCount)
    {
        Sql<ISqlContext> sql = Sql();

        sql = isCount
            ? sql.SelectCount()
            : sql.Select<WebhookDto>();

        sql
            .From<WebhookDto>();

        return sql;
    }

    protected override string GetBaseWhereClause() => $"{Constants.DatabaseSchema.Tables.Webhook}.id = @id";

    protected override IEnumerable<string> GetDeleteClauses()
    {
        var list = new List<string>
        {
            $"DELETE FROM {Constants.DatabaseSchema.Tables.Webhook} WHERE id = @id",
        };
        return list;
    }

    private static Webhook DtoToEntity(WebhookDto dto)
    {
        Webhook entity = WebhookFactory.BuildEntity(dto);

        // reset dirty initial properties (U4-1946)
        entity.ResetDirtyProperties(false);

        return entity;
    }
}
