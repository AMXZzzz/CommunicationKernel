// -----------------------------------------------------------------------------
// 文件: GlobalUsings.cs
// 层级: UI 层 — WPF
// 作用: 全局引入共享 gRPC 客户端命名空间，避免每个文件重复 using Hosting.Sdk。
//
// HostingClient 及各 DTO（RouteDto / ReadResultDto 等）原本定义在本项目的
// Services 命名空间下，与 Blazor 端各持一份。现已下沉到
// CommunicationKernel.Hosting.Sdk 由两端共用。
// 这些类型在本项目里散布于 ViewModel、服务与代码后置文件共十余处，
// 集中在此声明一次，胜过在每个文件顶部各加一行。
// -----------------------------------------------------------------------------

global using CommunicationKernel.Hosting.Sdk;
