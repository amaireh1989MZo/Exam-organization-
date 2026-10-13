-- مخطط أولي للمراجعة مع مسؤول قواعد البيانات؛ لا تشغّله في الإنتاج دون اعتماد.
CREATE TABLE dbo.SourceFiles (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    RelativePath NVARCHAR(2048) NOT NULL,
    Extension NVARCHAR(32) NULL,
    SizeBytes BIGINT NULL,
    LastWriteUtc DATETIME2 NULL,
    ContentHash CHAR(64) NULL,
    Status NVARCHAR(32) NOT NULL DEFAULT 'Pending',
    Attempts INT NOT NULL DEFAULT 0,
    LastErrorCode NVARCHAR(100) NULL,
    LastErrorAtUtc DATETIME2 NULL,
    DiscoveredAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    ProcessedAtUtc DATETIME2 NULL,
    CONSTRAINT UQ_SourceFiles_RelativePath UNIQUE (RelativePath)
);
CREATE INDEX IX_SourceFiles_Status_Id ON dbo.SourceFiles(Status, Id);

CREATE TABLE dbo.ProcessingRuns (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    RunType NVARCHAR(50) NOT NULL,
    Status NVARCHAR(32) NOT NULL,
    StartedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    FinishedAtUtc DATETIME2 NULL,
    FilesSeen BIGINT NOT NULL DEFAULT 0,
    FilesSucceeded BIGINT NOT NULL DEFAULT 0,
    FilesFailed BIGINT NOT NULL DEFAULT 0,
    Notes NVARCHAR(1000) NULL
);

CREATE TABLE dbo.Questions (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    StableQuestionKey NVARCHAR(200) NOT NULL,
    SourceFileId BIGINT NOT NULL,
    OriginalOrder INT NULL,
    OriginalCode NVARCHAR(200) NULL,
    OriginalText NVARCHAR(MAX) NOT NULL,
    OriginalOptionsJson NVARCHAR(MAX) NULL,
    OriginalAnswer NVARCHAR(MAX) NULL,
    Format NVARCHAR(32) NULL,
    ExtractedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Questions_SourceFiles FOREIGN KEY (SourceFileId) REFERENCES dbo.SourceFiles(Id),
    CONSTRAINT UQ_Questions_StableQuestionKey UNIQUE (StableQuestionKey)
);

CREATE TABLE dbo.CompetencyFrameworks (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    FrameworkName NVARCHAR(300) NOT NULL,
    VersionLabel NVARCHAR(100) NULL,
    SourceFilePath NVARCHAR(2048) NOT NULL,
    ImportedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE dbo.CompetencyIndicators (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    FrameworkId BIGINT NOT NULL,
    MainDomain NVARCHAR(500) NOT NULL,
    SubDomain NVARCHAR(500) NULL,
    IndicatorCode NVARCHAR(200) NULL,
    IndicatorName NVARCHAR(1000) NOT NULL,
    CONSTRAINT FK_Indicators_Framework FOREIGN KEY (FrameworkId) REFERENCES dbo.CompetencyFrameworks(Id)
);

CREATE TABLE dbo.QuestionMatches (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    QuestionId BIGINT NOT NULL,
    IndicatorId BIGINT NULL,
    MatchScore DECIMAL(5,4) NULL,
    MatchStatus NVARCHAR(32) NOT NULL DEFAULT 'NeedsReview',
    Rationale NVARCHAR(2000) NULL,
    ReviewedBy NVARCHAR(256) NULL,
    ReviewedAtUtc DATETIME2 NULL,
    FrameworkVersion NVARCHAR(100) NULL,
    CreatedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Matches_Questions FOREIGN KEY (QuestionId) REFERENCES dbo.Questions(Id),
    CONSTRAINT FK_Matches_Indicators FOREIGN KEY (IndicatorId) REFERENCES dbo.CompetencyIndicators(Id)
);
CREATE INDEX IX_QuestionMatches_Status ON dbo.QuestionMatches(MatchStatus);
