﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Newtonsoft.Json;
using WaterGunBeetles.Internal;

namespace WaterGunBeetles.Client.Aws
{
  public class LambdaControlPlane : IControlPlane
  {

    readonly string _controlPlaneTopicArn;
    readonly int _provisionedConcucrrency;
    readonly Action<object> _details;
    readonly Lazy<AmazonSimpleNotificationServiceClient> _snsClient;

    public LambdaControlPlane(string controlPlaneTopicArn,
      int provisionedConcucrrency,
      Func<IEnumerable<PublishRequest>, TimeSpan, CancellationToken, Task> publisher = null,
      Action<object> detailsLog = null)
    {
      _controlPlaneTopicArn = controlPlaneTopicArn;
      _provisionedConcucrrency = provisionedConcucrrency;
      _details = detailsLog ?? (_ => { });
      Publisher = publisher ?? SnsPublisher;
      _snsClient = new Lazy<AmazonSimpleNotificationServiceClient>(() =>
        new AmazonSimpleNotificationServiceClient(RegionEndpoint.EUWest2));
    }

    public async Task SetLoad(LoadTestStepContext ctx)
    {
      var lambdaRequestCounts = JourneyCalcuations.JourneyCounts(
        _provisionedConcucrrency,
        ctx.RequestsPerSecond,
        ctx.Duration.TotalSeconds,
        12);

      var publishRequests = new PublishRequest[lambdaRequestCounts.Count];

      for (var i = 0; i < lambdaRequestCounts.Count; i++)
      {
        var count = lambdaRequestCounts[i];
        publishRequests[i] = new PublishRequest
        {
          TopicArn = _controlPlaneTopicArn,
          Message = JsonConvert.SerializeObject(new LambdaRequest
          {
            RequestCount = count,
            Journeys = await ctx.StoryTeller(count),
            Duration = ctx.Duration
          })
        };
      }

      await ctx.PublishAsync(publishRequests, ctx.Duration, ctx.Cancel);
    }

    public Func<IEnumerable<PublishRequest>, TimeSpan, CancellationToken, Task> Publisher { get; set; }

    async Task SnsPublisher(IEnumerable<PublishRequest> publishRequests, TimeSpan duration, CancellationToken cancellationToken)
    {
      var publishTime = duration / publishRequests.Count();
      var sw = Stopwatch.StartNew();
      var scheduler = new TaskSchedulingInterval();
      foreach (var r in publishRequests)
      {
        scheduler.Start();
        await _snsClient.Value.PublishAsync(r, cancellationToken);
        await scheduler.WaitFor(publishTime, cancellationToken);
      }

      _details($"[VERBOSE] Requested {publishRequests.Count()} invocations in {sw.Elapsed}");
    }
  }
}