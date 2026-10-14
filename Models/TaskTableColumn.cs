namespace NoisLogTray;

// The Work board's table columns, left to right.
internal enum TaskTableColumn
{
    Key,
    Summary,
    Status,
    Due,
    Eta,
    Priority,
    Logged,
    PrMain, // the ticket's PRs to main, across its repos
    PrDev,  // and to development (the "<KEY>-dev" branch)
}
