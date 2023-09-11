﻿function webhooksResource($q, $http, umbRequestHelper) {
  return {
    getByKey(key) {
      return umbRequestHelper.resourcePromise(
        $http.get(umbRequestHelper.getApiUrl('webhooksApiBaseUrl', 'GetByKey', {key})),
        'Failed to get webhooks',
      );
    },
    getAll(pageNumber, pageSize) {
      return umbRequestHelper.resourcePromise(
        $http.get(umbRequestHelper.getApiUrl('webhooksApiBaseUrl', 'GetAll', {pageNumber, pageSize})),
        'Failed to get webhooks',
      );
    },
    create(webhook) {
      return umbRequestHelper.resourcePromise(
        $http.post(umbRequestHelper.getApiUrl('webhooksApiBaseUrl', 'Create'), webhook),
        `Failed to save webhook id ${webhook.id}`,
      );
    },
    delete(key) {
      return umbRequestHelper.resourcePromise(
        $http.delete(umbRequestHelper.getApiUrl('webhooksApiBaseUrl', 'Delete', {key})),
        `Failed to delete webhook id ${id}`,
      );
    },
    getAllEvents() {
      return umbRequestHelper.resourcePromise(
        $http.get(umbRequestHelper.getApiUrl('webhooksApiBaseUrl', 'GetEvents')),
        'Failed to get events',
      );
    },
  };
}
angular.module('umbraco.resources').factory('webhooksResource', webhooksResource);
