﻿using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OnnxStack.Core;
using OnnxStack.Core.Config;
using OnnxStack.Core.Services;
using OnnxStack.StableDiffusion.Common;
using OnnxStack.StableDiffusion.Config;
using OnnxStack.StableDiffusion.Enums;
using OnnxStack.StableDiffusion.Models;
using OnnxStack.StableDiffusion.Schedulers.LatentConsistency;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OnnxStack.StableDiffusion.Diffusers.LatentConsistency
{
    public abstract class LatentConsistencyDiffuser : DiffuserBase, IDiffuser
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LatentConsistencyDiffuser"/> class.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        /// <param name="onnxModelService">The onnx model service.</param>
        public LatentConsistencyDiffuser(IOnnxModelService onnxModelService, IPromptService promptService, ILogger<LatentConsistencyDiffuser> logger)
            : base(onnxModelService, promptService, logger) { }


        /// <summary>
        /// Gets the type of the pipeline.
        /// </summary>
        public override DiffuserPipelineType PipelineType => DiffuserPipelineType.LatentConsistency;


        /// <summary>
        /// Runs the stable diffusion loop
        /// </summary>
        /// <param name="modelOptions"></param>
        /// <param name="promptOptions">The prompt options.</param>
        /// <param name="schedulerOptions">The scheduler options.</param>
        /// <param name="progressCallback"></param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public override Task<DenseTensor<float>> DiffuseAsync(IModelOptions modelOptions, PromptOptions promptOptions, SchedulerOptions schedulerOptions, Action<int, int> progressCallback = null, CancellationToken cancellationToken = default)
        {
            // LCM does not support negative prompting
            promptOptions.NegativePrompt = string.Empty;
            return base.DiffuseAsync(modelOptions, promptOptions, schedulerOptions, progressCallback, cancellationToken);
        }


        /// <summary>
        /// Runs the stable diffusion batch loop
        /// </summary>
        /// <param name="modelOptions">The model options.</param>
        /// <param name="promptOptions">The prompt options.</param>
        /// <param name="schedulerOptions">The scheduler options.</param>
        /// <param name="batchOptions">The batch options.</param>
        /// <param name="progressCallback">The progress callback.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public override IAsyncEnumerable<BatchResult> DiffuseBatchAsync(IModelOptions modelOptions, PromptOptions promptOptions, SchedulerOptions schedulerOptions, BatchOptions batchOptions, Action<int, int, int, int> progressCallback = null, CancellationToken cancellationToken = default)
        {
            // LCM does not support negative prompting
            promptOptions.NegativePrompt = string.Empty;
            return base.DiffuseBatchAsync(modelOptions, promptOptions, schedulerOptions, batchOptions, progressCallback, cancellationToken);
        }

        protected override bool ShouldPerformGuidance(SchedulerOptions schedulerOptions)
        {
            // LCM does not support Guidance
            return false;
        }

        /// <summary>
        /// Runs the scheduler steps.
        /// </summary>
        /// <param name="modelOptions">The model options.</param>
        /// <param name="promptOptions">The prompt options.</param>
        /// <param name="schedulerOptions">The scheduler options.</param>
        /// <param name="promptEmbeddings">The prompt embeddings.</param>
        /// <param name="performGuidance">if set to <c>true</c> [perform guidance].</param>
        /// <param name="progressCallback">The progress callback.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        protected override async Task<DenseTensor<float>> SchedulerStepAsync(IModelOptions modelOptions, PromptOptions promptOptions, SchedulerOptions schedulerOptions, DenseTensor<float> promptEmbeddings, bool performGuidance, Action<int, int> progressCallback = null, CancellationToken cancellationToken = default)
        {
            // Get Scheduler
            using (var scheduler = GetScheduler(schedulerOptions))
            {
                // Get timesteps
                var timesteps = GetTimesteps(schedulerOptions, scheduler);

                // Create latent sample
                var latents = await PrepareLatentsAsync(modelOptions, promptOptions, schedulerOptions, scheduler, timesteps);

                // Get Guidance Scale Embedding
                var guidanceEmbeddings = GetGuidanceScaleEmbedding(schedulerOptions.GuidanceScale);

                // Denoised result
                DenseTensor<float> denoised = null;

                // Get Model metadata
                var inputNames = _onnxModelService.GetInputNames(modelOptions, OnnxModelType.Unet);
                var outputNames = _onnxModelService.GetOutputNames(modelOptions, OnnxModelType.Unet);
                var inputMetaData = _onnxModelService.GetInputMetadata(modelOptions, OnnxModelType.Unet);
                var outputMetaData = _onnxModelService.GetOutputMetadata(modelOptions, OnnxModelType.Unet);
                var timestepMetaData = inputMetaData[inputNames[1]];
                var outputTensorMetaData = outputMetaData[outputNames[0]];

                // Loop though the timesteps
                var step = 0;
                foreach (var timestep in timesteps)
                {
                    step++;
                    var stepTime = Stopwatch.GetTimestamp();
                    cancellationToken.ThrowIfCancellationRequested();

                    // Create input tensor.
                    var inputTensor = scheduler.ScaleInput(latents, timestep);

                    var outputChannels = 1;
                    var outputDimension = schedulerOptions.GetScaledDimension(outputChannels);
                    using (var outputTensorValue = outputTensorMetaData.CreateOutputBuffer(outputDimension))
                    using (var inputTensorValue = inputTensor.ToOrtValue(outputTensorMetaData))
                    using (var promptTensorValue = promptEmbeddings.ToOrtValue(outputTensorMetaData))
                    using (var guidanceTensorValue = guidanceEmbeddings.ToOrtValue(outputTensorMetaData))
                    using (var timestepTensorValue = CreateTimestepNamedOrtValue(timestepMetaData, timestep))
                    {
                        var inputs = new Dictionary<string, OrtValue>
                        {
                            { inputNames[0], inputTensorValue },
                            { inputNames[1], timestepTensorValue },
                            { inputNames[2], promptTensorValue },
                            { inputNames[3], guidanceTensorValue }
                        };

                        var outputs = new Dictionary<string, OrtValue> { { outputNames[0], outputTensorValue } };
                        var results = await _onnxModelService.RunInferenceAsync(modelOptions, OnnxModelType.Unet, inputs, outputs);
                        using (var result = results.First())
                        {
                            var noisePred = outputTensorValue.ToDenseTensor();

                            // Scheduler Step
                            var schedulerResult = scheduler.Step(noisePred, timestep, latents);

                            latents = schedulerResult.Result;
                            denoised = schedulerResult.SampleData;
                        }
                    }

                    progressCallback?.Invoke(step, timesteps.Count);
                    _logger?.LogEnd(LogLevel.Debug, $"Step {step}/{timesteps.Count}", stepTime);
                }

                // Decode Latents
                return await DecodeLatentsAsync(modelOptions, promptOptions, schedulerOptions, denoised);
            }
        }


        /// <summary>
        /// Gets the scheduler.
        /// </summary>
        /// <param name="prompt"></param>
        /// <param name="options">The options.</param>
        /// <returns></returns>
        protected override IScheduler GetScheduler(SchedulerOptions options)
        {
            return options.SchedulerType switch
            {
                SchedulerType.LCM => new LCMScheduler(options),
                _ => default
            };
        }


        /// <summary>
        /// Gets the guidance scale embedding.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="embeddingDim">The embedding dim.</param>
        /// <returns></returns>
        protected DenseTensor<float> GetGuidanceScaleEmbedding(float guidance, int embeddingDim = 256)
        {
            var scale = guidance - 1f;
            var halfDim = embeddingDim / 2;
            float log = MathF.Log(10000.0f) / (halfDim - 1);
            var emb = Enumerable.Range(0, halfDim)
                .Select(x => MathF.Exp(x * -log))
                .ToArray();
            var embSin = emb.Select(MathF.Sin).ToArray();
            var embCos = emb.Select(MathF.Cos).ToArray();
            var result = new DenseTensor<float>(new[] { 1, 2 * halfDim });
            for (int i = 0; i < halfDim; i++)
            {
                result[0, i] = embSin[i];
                result[0, i + halfDim] = embCos[i];
            }
            return result;
        }
    }
}
