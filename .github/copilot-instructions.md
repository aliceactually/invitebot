# Copilot Instructions — invitebot

## Project layout gotcha: tests folder

The solution has a single SDK-style project at `invitebot.csproj` (the bot)
plus a sibling xUnit project at `tests/invitebot.tests.csproj`. The bot
project explicitly excludes the `tests/` folder from its default compile
glob:

```xml
<ItemGroup>
  <Compile Remove="tests/**" />
  <None Remove="tests/**" />
  <Content Remove="tests/**" />
</ItemGroup>
```

**Visual Studio (2026 / 17.x) will auto-add `<Compile Include="tests\…" />`
entries to `invitebot.csproj` whenever a new file is created under `tests/`.**
An explicit `Include` overrides the `Remove` glob, so the main project then
tries to compile the test sources and the build fails with `CS0246` for
`Fact`, `Theory`, `InlineData`, `Xunit`, etc. (xUnit is referenced only by
the test project).

### Required workflow when adding a test file

1. Create the new file under `tests/` as normal.
2. **Immediately open `invitebot.csproj` and remove any
   `<Compile Include="tests\…" />` line** (and the empty `<ItemGroup>` it
   sits in) that VS inserted. The block typically looks like:

   ```xml
   <ItemGroup>
     <Compile Include="tests\NewTestFile.cs" />
   </ItemGroup>
   ```

3. Run `dotnet test tests/invitebot.tests.csproj --nologo` to confirm the
   build is clean and the new test runs.

If a CI/local build suddenly starts emitting unresolved-xUnit-symbol errors
attributed to `[invitebot.csproj]`, this is almost always the cause — check
`invitebot.csproj` for a stray `Compile Include` under `tests\` first.
