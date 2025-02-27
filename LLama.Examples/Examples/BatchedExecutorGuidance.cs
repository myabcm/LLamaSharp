﻿using LLama.Batched;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Spectre.Console;

namespace LLama.Examples.Examples;

/// <summary>
/// This demonstrates using a batch to generate two sequences and then using one
/// sequence as the negative guidance ("classifier free guidance") for the other.
/// </summary>
public class BatchedExecutorGuidance
{
    private const int n_len = 32;

    public static async Task Run()
    {
        string modelPath = UserSettings.GetModelPath();

        var parameters = new ModelParams(modelPath);
        using var model = LLamaWeights.LoadFromFile(parameters);

        var positivePrompt = AnsiConsole.Ask("Positive Prompt (or ENTER for default):", "My favourite colour is").Trim();
        var negativePrompt = AnsiConsole.Ask("Negative Prompt (or ENTER for default):", "I hate the colour red. My favourite colour is").Trim();
        var weight = AnsiConsole.Ask("Guidance Weight (or ENTER for default):", 2.0f);

        // Create an executor that can evaluate a batch of conversations together
        using var executor = new BatchedExecutor(model, parameters);

        // Print some info
        var name = executor.Model.Metadata.GetValueOrDefault("general.name", "unknown model name");
        Console.WriteLine($"Created executor with model: {name}");

        // Load the two prompts into two conversations
        using var guided = executor.Prompt(positivePrompt);
        using var guidance = executor.Prompt(negativePrompt);

        // Run inference to evaluate prompts
        await AnsiConsole
             .Status()
             .Spinner(Spinner.Known.Line)
             .StartAsync("Evaluating Prompts...", _ => executor.Infer());

        // Fork the "guided" conversation. We'll run this one without guidance for comparison
        using var unguided = guided.Fork();

        // Run inference loop
        var unguidedSampler = new GuidedSampler(null, weight);
        var unguidedDecoder = new StreamingTokenDecoder(executor.Context);
        var guidedSampler = new GuidedSampler(guidance, weight);
        var guidedDecoder = new StreamingTokenDecoder(executor.Context);
        await AnsiConsole
           .Progress()
           .StartAsync(async progress =>
            {
                var reporter = progress.AddTask("Running Inference", maxValue: n_len);

                for (var i = 0; i < n_len; i++)
                {
                    if (i != 0)
                        await executor.Infer();

                    // Sample from the "unguided" conversation. This is just a conversation using the same prompt, without any
                    // guidance. This serves as a comparison to show the effect of guidance.
                    var u = unguidedSampler.Sample(executor.Context.NativeHandle, unguided.Sample(), Array.Empty<LLamaToken>());
                    unguidedDecoder.Add(u);
                    unguided.Prompt(u);

                    // Sample from the "guided" conversation. This sampler will internally use the "guidance" conversation
                    // to steer the conversation. See how this is done in GuidedSampler.ProcessLogits (bottom of this file).
                    var g = guidedSampler.Sample(executor.Context.NativeHandle, guided.Sample(), Array.Empty<LLamaToken>());
                    guidedDecoder.Add(g);

                    // Use this token to advance both guided _and_ guidance. Keeping them in sync (except for the initial prompt).
                    guided.Prompt(g);
                    guidance.Prompt(g);

                    // Early exit if we reach the natural end of the guided sentence
                    if (g == model.EndOfSentenceToken)
                        break;

                    // Update progress bar
                    reporter.Increment(1);
                }
            });

        AnsiConsole.MarkupLine($"[green]Unguided:[/][white]{unguidedDecoder.Read().ReplaceLineEndings(" ")}[/]");
        AnsiConsole.MarkupLine($"[green]Guided:[/][white]{guidedDecoder.Read().ReplaceLineEndings(" ")}[/]");
    }

    private class GuidedSampler(Conversation? guidance, float weight)
        : BaseSamplingPipeline
    {
        public override void Accept(SafeLLamaContextHandle ctx, LLamaToken token)
        {
        }

        public override ISamplingPipeline Clone()
        {
            throw new NotSupportedException();
        }

        protected override void ProcessLogits(SafeLLamaContextHandle ctx, Span<float> logits, ReadOnlySpan<LLamaToken> lastTokens)
        {
            if (guidance == null)
                return;

            // Get the logits generated by the guidance sequences
            var guidanceLogits = guidance.Sample();

            // Use those logits to guide this sequence
            NativeApi.llama_sample_apply_guidance(ctx, logits, guidanceLogits, weight);
        }

        protected override LLamaToken ProcessTokenDataArray(SafeLLamaContextHandle ctx, LLamaTokenDataArray candidates, ReadOnlySpan<LLamaToken> lastTokens)
        {
            candidates.Temperature(ctx, 0.8f);
            candidates.TopK(ctx, 25);

            return candidates.SampleToken(ctx);
        }
    }
}