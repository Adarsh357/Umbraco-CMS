﻿using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Exceptions;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Entities;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Persistence.Querying;
using Umbraco.Cms.Core.Persistence.Repositories;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services.Changes;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Extensions;

namespace Umbraco.Cms.Core.Services;

public class ContentPublishingService : IContentPublishingService
{
    private readonly ICoreScopeProvider _coreScopeProvider;
    private readonly IContentService _contentService;
    private readonly ILanguageService _languageService;
    private readonly ICultureImpactFactory _cultureImpactFactory;
    private readonly IDocumentRepository _documentRepository;
    private readonly IEventMessagesFactory _eventMessagesFactory;
    private readonly IAuditRepository _auditRepository;
    private readonly ILogger<ContentPublishingService> _logger;

    public ContentPublishingService(
        ICoreScopeProvider coreScopeProvider,
        IContentService contentService,
        ILanguageService languageService,
        ICultureImpactFactory cultureImpactFactory,
        IDocumentRepository documentRepository,
        IEventMessagesFactory eventMessagesFactory,
        IAuditRepository auditRepository,
        ILogger<ContentPublishingService> logger)
    {
        _coreScopeProvider = coreScopeProvider;
        _contentService = contentService;
        _languageService = languageService;
        _cultureImpactFactory = cultureImpactFactory;
        _documentRepository = documentRepository;
        _eventMessagesFactory = eventMessagesFactory;
        _auditRepository = auditRepository;
        _logger = logger;
    }

    /*
    Things that can go wrong when publishing a branch (on any given culture):
    - cannot be published because it is invalid (any property was configured invalid)
    - canceled by event
    - mandatory culture missing
    - has expired
    - awaiting release (scheduled publishing)
    - is trashed
    - path not published (aka, parent node could not publish, and we're trying to publish the child)
    */


    public async Task<Attempt<ContentPublishingOperationStatus>> PublishAsync(Guid id, Guid userKey,
        string culture = "*")
    {
        IContent? foundContent = _contentService.GetById(id);

        if (foundContent is null)
        {
            return Attempt.Fail(ContentPublishingOperationStatus.ContentNotFound);
        }

        // cannot accept invariant (null or empty) culture for variant content type
        // cannot accept a specific culture for invariant content type (but '*' is ok)
        if (foundContent.ContentType.VariesByCulture())
        {
            if (culture.IsNullOrWhiteSpace())
            {
                throw new NotSupportedException("Invariant culture is not supported by variant content types.");
            }
        }
        else
        {
            if (!culture.IsNullOrWhiteSpace() && culture != "*")
            {
                throw new NotSupportedException(
                    $"Culture \"{culture}\" is not supported by invariant content types.");
            }
        }

        using ICoreScope scope = _coreScopeProvider.CreateCoreScope();
        scope.WriteLock(Constants.Locks.ContentTree);

        IEnumerable<ILanguage> allLangs = await _languageService.GetAllAsync();

        // Change state to publishing
        foundContent.PublishedState = PublishedState.Publishing;

        // if culture is specific, first publish the invariant values, then publish the culture itself.
        // if culture is '*', then publish them all (including variants)

        // this will create the correct culture impact even if culture is * or null
        CultureImpact? impact =
            _cultureImpactFactory.Create(culture, IsDefaultCulture(allLangs, culture), foundContent);

        // publish the culture(s)
        // we don't care about the response here, this response will be rechecked below but we need to set the culture info values now.
        foundContent.PublishCulture(impact);

        ContentPublishingOperationStatus contentPublishingOperationStatus =
            Publish(foundContent, allLangs, userKey, scope);

        scope.Complete();

        if (contentPublishingOperationStatus is ContentPublishingOperationStatus.Success
            or ContentPublishingOperationStatus.SuccessPublishCulture)
        {
            return Attempt.Succeed(contentPublishingOperationStatus);
        }

        return Attempt.Fail(contentPublishingOperationStatus);
    }

    public async Task<Attempt<ContentPublishingOperationStatus>> PublishAsync(Guid id, Guid userKey, string[] cultures)
    {
        IContent? content = _contentService.GetById(id);

        if (content is null)
        {
            return Attempt.Fail(ContentPublishingOperationStatus.ContentNotFound);
        }

        if (content.Name != null && content.Name.Length > 255)
        {
            throw new InvalidOperationException("Name cannot be more than 255 characters in length.");
        }

        using ICoreScope scope = _coreScopeProvider.CreateCoreScope();
        scope.WriteLock(Constants.Locks.ContentTree);

        IEnumerable<ILanguage> allLangs = await _languageService.GetAllAsync();

        var varies = content.ContentType.VariesByCulture();

        if (cultures.Length == 0 && !varies)
        {
            // No cultures specified and doesn't vary, so publish it, else nothing to publish
            return await PublishAsync(id, userKey);
        }

        if (cultures.Any(x => x == null || x == "*"))
        {
            throw new InvalidOperationException(
                "Only valid cultures are allowed to be used in this method, wildcards or nulls are not allowed");
        }

        IEnumerable<CultureImpact> impacts =
            cultures.Select(x => _cultureImpactFactory.ImpactExplicit(x, IsDefaultCulture(allLangs, x)));

        // publish the culture(s)
        // we don't care about the response here, this response will be rechecked below but we need to set the culture info values now.
        foreach (CultureImpact impact in impacts)
        {
            content.PublishCulture(impact);
        }

        ContentPublishingOperationStatus contentPublishingOperationStatus = Publish(content, allLangs, userKey, scope);

        scope.Complete();

        if (contentPublishingOperationStatus is ContentPublishingOperationStatus.Success
            or ContentPublishingOperationStatus.SuccessPublishCulture)
        {
            Attempt.Succeed(contentPublishingOperationStatus);
        }

        return Attempt.Fail(contentPublishingOperationStatus);
    }

    public async Task<Attempt<ContentPublishingOperationStatus>> PublishBranch(Guid id, bool force, Guid userKey, string culture = "*")
    {
        IContent? content = _contentService.GetById(id);
        if (content is null)
        {
            return Attempt.Fail(ContentPublishingOperationStatus.ContentNotFound);
        }

        // note: EditedValue and PublishedValue are objects here, so it is important to .Equals()
        // and not to == them, else we would be comparing references, and that is a bad thing

        // determines whether the document is edited, and thus needs to be published,
        // for the specified culture (it may be edited for other cultures and that
        // should not trigger a publish).

        // determines cultures to be published
        // can be: null (content is not impacted), an empty set (content is impacted but already published), or cultures
        HashSet<string>? ShouldPublish(IContent c)
        {
            var isRoot = c.Id == content.Id;
            HashSet<string>? culturesToPublish = null;

            // invariant content type
            if (!c.ContentType.VariesByCulture())
            {
                return SaveAndPublishBranch_ShouldPublish(ref culturesToPublish, "*", c.Published, c.Edited, isRoot, force);
            }

            // variant content type, specific culture
            if (culture != "*")
            {
                return SaveAndPublishBranch_ShouldPublish(ref culturesToPublish, culture, c.IsCulturePublished(culture), c.IsCultureEdited(culture), isRoot, force);
            }

            // variant content type, all cultures
            if (c.Published)
            {
                // then some (and maybe all) cultures will be 'already published' (unless forcing),
                // others will have to 'republish this culture'
                foreach (var x in c.AvailableCultures)
                {
                    SaveAndPublishBranch_ShouldPublish(ref culturesToPublish, x, c.IsCulturePublished(x), c.IsCultureEdited(x), isRoot, force);
                }

                return culturesToPublish;
            }

            // if not published, publish if force/root else do nothing
            return force || isRoot
                ? new HashSet<string> { "*" } // "*" means 'publish all'
                : null; // null means 'nothing to do'
        }

        return Attempt.Succeed((await SaveAndPublishBranch(content, force, ShouldPublish, SaveAndPublishBranch_PublishCultures, userKey)).First());
    }

    public Task<Attempt<ContentPublishingOperationStatus>> PublishBranch(Guid id, bool force, Guid userKey, string[] cultures) => throw new NotImplementedException();

    internal async Task<IEnumerable<ContentPublishingOperationStatus>> SaveAndPublishBranch(
        IContent document,
        bool force,
        Func<IContent, HashSet<string>?> shouldPublish,
        Func<IContent, HashSet<string>, IReadOnlyCollection<ILanguage>, bool> publishCultures,
        Guid userKey)
    {
        if (shouldPublish == null)
        {
            throw new ArgumentNullException(nameof(shouldPublish));
        }

        if (publishCultures == null)
        {
            throw new ArgumentNullException(nameof(publishCultures));
        }

        EventMessages eventMessages = _eventMessagesFactory.Get();
        var results = new List<ContentPublishingOperationStatus>();
        var publishedDocuments = new List<IContent>();

        using (ICoreScope scope = _coreScopeProvider.CreateCoreScope())
        {
            scope.WriteLock(Constants.Locks.ContentTree);

            var allLangs = await _languageService.GetAllAsync();

            if (!document.HasIdentity)
            {
                throw new InvalidOperationException("Cannot not branch-publish a new document.");
            }

            PublishedState publishedState = document.PublishedState;
            if (publishedState == PublishedState.Publishing)
            {
                throw new InvalidOperationException("Cannot mix PublishCulture and SaveAndPublishBranch.");
            }

            // deal with the branch root - if it fails, abort
            ContentPublishingOperationStatus result = SaveAndPublishBranchItem(scope, document, shouldPublish, publishCultures, true, publishedDocuments, eventMessages, userKey, allLangs.ToList());
            results.Add(result);
            if (result is not ContentPublishingOperationStatus.Success)
            {
                return results;
            }

                // deal with descendants
            // if one fails, abort its branch
            var exclude = new HashSet<int>();

            int count;
            var page = 0;
            const int pageSize = 100;
            do
            {
                count = 0;

                // important to order by Path ASC so make it explicit in case defaults change
                // ReSharper disable once RedundantArgumentDefaultValue
                foreach (IContent d in GetPagedDescendants(document.Id, page, pageSize, out _, ordering: Ordering.By("Path", Direction.Ascending)))
                {
                    count++;

                    // if parent is excluded, exclude child too
                    if (exclude.Contains(d.ParentId))
                    {
                        exclude.Add(d.Id);
                        continue;
                    }

                    // no need to check path here, parent has to be published here
                    result = SaveAndPublishBranchItem(scope, d, shouldPublish, publishCultures, false, publishedDocuments, eventMessages, userKey, allLangs.ToList());
                    results.Add(result);
                    if (result != ContentPublishingOperationStatus.Success)
                    {
                        continue;
                    }

                    // if we could not publish the document, cut its branch
                    exclude.Add(d.Id);
                }

                page++;
            }
            while (count > 0);

            // TODO use mapped userkey here
            Audit(AuditType.Publish, -1, document.Id, "Branch published");

            // trigger events for the entire branch
            // (SaveAndPublishBranchOne does *not* do it)
            scope.Notifications.Publish(
                new ContentTreeChangeNotification(document, TreeChangeTypes.RefreshBranch, eventMessages));
            scope.Notifications.Publish(new ContentPublishedNotification(publishedDocuments, eventMessages, true));

            scope.Complete();
        }

        return results;
    }

    // shouldPublish: a function determining whether the document has changes that need to be published
    //  note - 'force' is handled by 'editing'
    // publishValues: a function publishing values (using the appropriate PublishCulture calls)
    private ContentPublishingOperationStatus SaveAndPublishBranchItem(
        ICoreScope scope,
        IContent document,
        Func<IContent, HashSet<string>?> shouldPublish,
        Func<IContent, HashSet<string>, IReadOnlyCollection<ILanguage>, bool> publishCultures,
        bool isRoot,
        ICollection<IContent> publishedDocuments,
        EventMessages evtMsgs,
        Guid userKey,
        IReadOnlyCollection<ILanguage> allLangs)
    {
        HashSet<string>? culturesToPublish = shouldPublish(document);

        // null = do not include
        if (culturesToPublish == null)
        {
            return ContentPublishingOperationStatus.FailedNothingToPublish;
        }

        // empty = already published
        if (culturesToPublish.Count == 0)
        {
            return ContentPublishingOperationStatus.SuccessPublishAlready;
        }

        var savingNotification = new ContentSavingNotification(document, evtMsgs);
        if (scope.Notifications.PublishCancelable(savingNotification))
        {
            return ContentPublishingOperationStatus.FailedPublishCancelledByEvent;
        }

        // publish & check if values are valid
        if (!publishCultures(document, culturesToPublish, allLangs))
        {
            // TODO: Based on this callback behavior there is no way to know which properties may have been invalid if this failed, see other results of FailedPublishContentInvalid
            return ContentPublishingOperationStatus.FailedPublishContentInvalid;
        }

        ContentPublishingOperationStatus result = Publish(document, allLangs, userKey, scope, true, isRoot);
        if (result == ContentPublishingOperationStatus.Success || result == ContentPublishingOperationStatus.SuccessPublishCulture)
        {
            publishedDocuments.Add(document);
        }

        return result;
    }

    // utility 'ShouldPublish' func used by SaveAndPublishBranch
    private HashSet<string>? SaveAndPublishBranch_ShouldPublish(ref HashSet<string>? cultures, string c, bool published, bool edited, bool isRoot, bool force)
    {
        // if published, republish
        if (published)
        {
            if (cultures == null)
            {
                cultures = new HashSet<string>(); // empty means 'already published'
            }

            if (edited)
            {
                cultures.Add(c); // <culture> means 'republish this culture'
            }

            return cultures;
        }

        // if not published, publish if force/root else do nothing
        if (!force && !isRoot)
        {
            return cultures; // null means 'nothing to do'
        }

        if (cultures == null)
        {
            cultures = new HashSet<string>();
        }

        cultures.Add(c); // <culture> means 'publish this culture'
        return cultures;
    }

    // utility 'PublishCultures' func used by SaveAndPublishBranch
    private bool SaveAndPublishBranch_PublishCultures(IContent content, HashSet<string> culturesToPublish, IReadOnlyCollection<ILanguage> allLangs)
    {
        return content.PublishCulture(_cultureImpactFactory.ImpactInvariant());
    }

    private bool IsDefaultCulture(IEnumerable<ILanguage>? langs, string culture) =>
        langs?.Any(x => x.IsDefault && x.IsoCode.InvariantEquals(culture)) ?? false;

    private ContentPublishingOperationStatus Publish(IContent content, IEnumerable<ILanguage> languages, Guid userKey, ICoreScope scope, bool branchOne = false, bool branchRoot = false)
    {
        var userId = -1;
        PublishResult? unpublishResult = null;
        EventMessages eventMessages = _eventMessagesFactory.Get();
        var isNew = !content.HasIdentity;
        var previouslyPublished = content.HasIdentity && content.Published;
        TreeChangeTypes changeType = isNew ? TreeChangeTypes.RefreshNode : TreeChangeTypes.RefreshBranch;
        var variesByCulture = content.ContentType.VariesByCulture();

        List<string>? culturesPublishing = variesByCulture
            ? content.PublishCultureInfos?.Values.Where(x => x.IsDirty()).Select(x => x.Culture).ToList()
            : null;

        IReadOnlyList<string>? culturesChanging = variesByCulture
            ? content.CultureInfos?.Values.Where(x => x.IsDirty()).Select(x => x.Culture).ToList()
            : null;

        // ensure that the document can be published, and publish handling events, business rules, etc
        ContentPublishingOperationStatus publishOperationStatus = StrategyCanPublish(
            scope,
            content, /*checkPath:*/
            !branchOne || branchRoot,
            culturesPublishing,
            languages,
            eventMessages);
        if (publishOperationStatus is ContentPublishingOperationStatus.Success)
        {
            // note: StrategyPublish flips the PublishedState to Publishing!
            publishOperationStatus = StrategyPublish(content, culturesPublishing, eventMessages);
        }
        else
        {
            // in a branch, just give up
            if (branchOne && !branchRoot)
            {
                return publishOperationStatus;
            }

            // // Check for mandatory culture missing, and then unpublish document as a whole
            // if (publishResult.Result == PublishResultType.FailedPublishMandatoryCultureMissing)
            // {
            //     publishing = false;
            //     unpublishing = content.Published; // if not published yet, nothing to do
            //
            //     // we may end up in a state where we won't publish nor unpublish
            //     // keep going, though, as we want to save anyways
            // }

            // reset published state from temp values (publishing, unpublishing) to original value
            // (published, unpublished) in order to save the document, unchanged - yes, this is odd,
            // but: (a) it means we don't reproduce the PublishState logic here and (b) setting the
            // PublishState to anything other than Publishing or Unpublishing - which is precisely
            // what we want to do here - throws
            content.Published = content.Published;
        }

        _documentRepository.Save(content);

        // raise the Saved event, always
        scope.Notifications.Publish(
            new ContentSavedNotification(content, eventMessages));

        // and succeeded, trigger events
        if (publishOperationStatus is not ContentPublishingOperationStatus.Success)
        {
            if (isNew == false && previouslyPublished == false)
            {
                changeType = TreeChangeTypes.RefreshBranch; // whole branch
            }
            else if (isNew == false && previouslyPublished)
            {
                changeType = TreeChangeTypes.RefreshNode; // single node
            }

            // invalidate the node/branch
            // for branches, handled by SaveAndPublishBranch
            if (!branchOne)
            {
                scope.Notifications.Publish(
                    new ContentTreeChangeNotification(content, changeType, eventMessages));
                scope.Notifications.Publish(
                    new ContentPublishedNotification(content, eventMessages));
            }

            // it was not published and now is... descendants that were 'published' (but
            // had an unpublished ancestor) are 're-published' ie not explicitly published
            // but back as 'published' nevertheless
            if (!branchOne && isNew == false && previouslyPublished == false && HasChildren(content.Id))
            {
                IContent[] descendants = GetPublishedDescendantsLocked(content).ToArray();
                scope.Notifications.Publish(
                    new ContentPublishedNotification(descendants, eventMessages));
            }

            switch (publishOperationStatus)
            {
                case ContentPublishingOperationStatus.Success:
                    Audit(AuditType.Publish, userId, content.Id);
                    break;
                case ContentPublishingOperationStatus.SuccessPublishCulture:
                    if (culturesPublishing != null)
                    {
                        var langs = string.Join(", ", languages
                            .Where(x => culturesPublishing.InvariantContains(x.IsoCode))
                            .Select(x => x.CultureName));
                        Audit(AuditType.PublishVariant, userId, content.Id, $"Published languages: {langs}", langs);
                    }

                    break;
            }

            return publishOperationStatus;
        }

        // should not happen
        if (branchOne && !branchRoot)
        {
            throw new PanicException("branchOne && !branchRoot - should not happen");
        }

        // if publishing didn't happen or if it has failed, we still need to log which cultures were saved
        if (!branchOne && (publishOperationStatus is not ContentPublishingOperationStatus.Success || publishOperationStatus is not ContentPublishingOperationStatus.SuccessPublishCulture))
        {
            if (culturesChanging != null)
            {
                var langs = string.Join(", ", languages
                    .Where(x => culturesChanging.InvariantContains(x.IsoCode))
                    .Select(x => x.CultureName));
                Audit(AuditType.SaveVariant, userId, content.Id, $"Saved languages: {langs}", langs);
            }
            else
            {
                Audit(AuditType.Save, userId, content.Id);
            }
        }

        // or, failed
        scope.Notifications.Publish(new ContentTreeChangeNotification(content, changeType, eventMessages));

        return publishOperationStatus!;
    }

    internal IEnumerable<IContent> GetPublishedDescendantsLocked(IContent content)
    {
        var pathMatch = content.Path + ",";
        IQuery<IContent> query = _coreScopeProvider.CreateQuery<IContent>()
            .Where(x => x.Id != content.Id && x.Path.StartsWith(pathMatch) /*&& x.Trashed == false*/);
        IEnumerable<IContent> contents = _documentRepository.Get(query);

        // beware! contents contains all published version below content
        // including those that are not directly published because below an unpublished content
        // these must be filtered out here
        var parents = new List<int> {content.Id};
        if (contents is not null)
        {
            foreach (IContent c in contents)
            {
                if (parents.Contains(c.ParentId))
                {
                    yield return c;
                    parents.Add(c.Id);
                }
            }
        }
    }

    private bool HasChildren(int id) => CountChildren(id) > 0;

    private int CountChildren(int parentId, string? contentTypeAlias = null)
    {
        using (ICoreScope scope = _coreScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            return _documentRepository.CountChildren(parentId, contentTypeAlias);
        }
    }

    private ContentPublishingOperationStatus StrategyPublish(
        IContent content,
        IReadOnlyCollection<string>? culturesPublishing,
        EventMessages eventMessages)
    {
        // change state to publishing
        content.PublishedState = PublishedState.Publishing;

        // if this is a variant then we need to log which cultures have been published/unpublished and return an appropriate result
        if (content.ContentType.VariesByCulture())
        {
            if (content.Published && culturesPublishing?.Count == 0)
            {
                return ContentPublishingOperationStatus.FailedNothingToPublish;
            }

            if (culturesPublishing?.Count > 0)
            {
                _logger.LogInformation(
                    "Document {ContentName} (id={ContentId}) cultures: {Cultures} have been published.",
                    content.Name,
                    content.Id,
                    string.Join(",", culturesPublishing));
            }

            return ContentPublishingOperationStatus.SuccessPublishCulture;
        }

        _logger.LogInformation("Document {ContentName} (id={ContentId}) has been published.", content.Name, content.Id);
        return ContentPublishingOperationStatus.Success;
    }

    private ContentPublishingOperationStatus StrategyCanPublish(
        ICoreScope scope,
        IContent content,
        bool checkPath,
        IEnumerable<string>? culturesPublishing,
        IEnumerable<ILanguage> allLangs,
        EventMessages eventMessages)
    {
        // raise Publishing notification
        if (scope.Notifications.PublishCancelable(
                new ContentPublishingNotification(content, eventMessages)))
        {
            _logger.LogInformation("Document {ContentName} (id={ContentId}) cannot be published: {Reason}", content.Name, content.Id, "publishing was cancelled");
            return ContentPublishingOperationStatus.FailedCancelledByEvent;
        }

        var variesByCulture = content.ContentType.VariesByCulture();

        // If it's null it's invariant
        CultureImpact[] impactsToPublish = culturesPublishing == null
            ? new[] {_cultureImpactFactory.ImpactInvariant()}
            : culturesPublishing.Select(x =>
                    _cultureImpactFactory.ImpactExplicit(
                        x,
                        allLangs.Any(lang => lang.IsoCode.InvariantEquals(x) && lang.IsMandatory)))
                .ToArray();

        // publish the culture(s)
        if (!impactsToPublish.All(content.PublishCulture))
        {
            return ContentPublishingOperationStatus.FailedContentInvalid;
        }

        // Check if mandatory languages fails, if this fails it will mean anything that the published flag on the document will
        // be changed to Unpublished and any culture currently published will not be visible.
        if (variesByCulture)
        {
            if (culturesPublishing == null)
            {
                throw new InvalidOperationException(
                    "Internal error, variesByCulture but culturesPublishing is null.");
            }

            if (content.Published && culturesPublishing.Any() is false)
            {
                // no published cultures = cannot be published
                // there will be nothing to publish
                return ContentPublishingOperationStatus.FailedContentInvalid;
            }

            // missing mandatory culture = cannot be published
            IEnumerable<string> mandatoryCultures = allLangs.Where(x => x.IsMandatory).Select(x => x.IsoCode);
            var mandatoryMissing = mandatoryCultures.Any(x =>
                !content.PublishedCultures.Contains(x, StringComparer.OrdinalIgnoreCase));
            if (mandatoryMissing)
            {
                return ContentPublishingOperationStatus.FailedMandatoryCultureMissing;
            }
        }

        // ensure that the document has published values
        // either because it is 'publishing' or because it already has a published version
        if (content.PublishedState != PublishedState.Publishing && content.PublishedVersionId == 0)
        {
            _logger.LogInformation(
                "Document {ContentName} (id={ContentId}) cannot be published: {Reason}",
                content.Name,
                content.Id,
                "document does not have published values");
            return ContentPublishingOperationStatus.FailedNothingToPublish;
        }

        ContentScheduleCollection contentSchedule = _documentRepository.GetContentSchedule(content.Id);

        // loop over each culture publishing - or string.Empty for invariant
        foreach (var culture in culturesPublishing ?? new[] {string.Empty})
        {
            // ensure that the document status is correct
            // note: culture will be string.Empty for invariant
            switch (content.GetStatus(contentSchedule, culture))
            {
                case ContentStatus.Expired:
                    if (!variesByCulture)
                    {
                        _logger.LogInformation(
                            "Document {ContentName} (id={ContentId}) cannot be published: {Reason}", content.Name, content.Id, "document has expired");
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Document {ContentName} (id={ContentId}) culture {Culture} cannot be published: {Reason}", content.Name, content.Id, culture, "document culture has expired");
                    }

                    return !variesByCulture
                        ? ContentPublishingOperationStatus.FailedHasExpired
                        : ContentPublishingOperationStatus.FailedCultureHasExpired;

                case ContentStatus.AwaitingRelease:
                    if (!variesByCulture)
                    {
                        _logger.LogInformation(
                            "Document {ContentName} (id={ContentId}) cannot be published: {Reason}",
                            content.Name,
                            content.Id,
                            "document is awaiting release");
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Document {ContentName} (id={ContentId}) culture {Culture} cannot be published: {Reason}",
                            content.Name,
                            content.Id,
                            culture,
                            "document is culture awaiting release");
                    }

                    return !variesByCulture
                        ? ContentPublishingOperationStatus.FailedAwaitingRelease
                        : ContentPublishingOperationStatus.FailedCultureAwaitingRelease;

                case ContentStatus.Trashed:
                    _logger.LogInformation(
                        "Document {ContentName} (id={ContentId}) cannot be published: {Reason}",
                        content.Name,
                        content.Id,
                        "document is trashed");
                    return ContentPublishingOperationStatus.FailedIsTrashed;
            }
        }

        if (checkPath)
        {
            // check if the content can be path-published
            // root content can be published
            // else check ancestors - we know we are not trashed
            var pathIsOk = content.ParentId == Constants.System.Root || IsPathPublished(GetParent(content));
            if (!pathIsOk)
            {
                _logger.LogInformation(
                    "Document {ContentName} (id={ContentId}) cannot be published: {Reason}",
                    content.Name,
                    content.Id,
                    "parent is not published");
                return ContentPublishingOperationStatus.FailedPathNotPublished;
            }
        }

        return ContentPublishingOperationStatus.Success;
    }

    public IContent? GetParent(IContent? content)
    {
        if (content?.ParentId == Constants.System.Root || content?.ParentId == Constants.System.RecycleBinContent ||
            content is null)
        {
            return null;
        }

        return GetById(content.ParentId);
    }

    public IContent? GetById(int id)
    {
        using (ICoreScope scope = _coreScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            return _documentRepository.Get(id);
        }
    }

    private bool IsPathPublished(IContent? content)
    {
        using (ICoreScope scope = _coreScopeProvider.CreateCoreScope(autoComplete: true))
        {
            scope.ReadLock(Constants.Locks.ContentTree);
            return _documentRepository.IsPathPublished(content);
        }
    }

    private void Audit(AuditType type, int userId, int objectId, string? message = null, string? parameters = null) =>
        _auditRepository.Save(new AuditItem(objectId, type, userId, UmbracoObjectTypes.Document.GetName(), message,
            parameters));
}
