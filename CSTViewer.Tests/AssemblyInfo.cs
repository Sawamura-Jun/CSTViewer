using Xunit;

// WPFの共有リソースを複数のSTAから同時に初期化しないよう、テストを直列実行する。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
