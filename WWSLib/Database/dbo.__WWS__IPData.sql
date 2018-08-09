CREATE TABLE [dbo].[__WWS__IPData] (
    [HostID]        BIGINT           NOT NULL,
    [ISP]           NVARCHAR (256)   DEFAULT ('') NOT NULL,
    [Organization]  NVARCHAR (256)   DEFAULT ('') NOT NULL,
    [CountryCode]   VARCHAR (2)      DEFAULT ('??') NOT NULL,
    [Country]       VARCHAR (128)    DEFAULT ('') NOT NULL,
    [RegionCode]    VARCHAR (2)      DEFAULT ('??') NOT NULL,
    [Region]        VARCHAR (128)    DEFAULT ('') NOT NULL,
    [City]          NVARCHAR (128)   DEFAULT ('') NOT NULL,
    [ZipCode]       INT              DEFAULT ((0)) NOT NULL,
    [Latitude]      DECIMAL (25, 18) DEFAULT ((0)) NOT NULL,
    [Longitude]     DECIMAL (25, 18) DEFAULT ((0)) NOT NULL,
    [Timezone]      NVARCHAR (256)   DEFAULT ('') NOT NULL,
    [AliasName]     NVARCHAR (256)   DEFAULT ('') NOT NULL,
    [Hostname]      NVARCHAR (256)   DEFAULT ('') NOT NULL,
    [LastUpdateUTC] DATETIME         NULL,
    PRIMARY KEY CLUSTERED ([HostID] ASC),
    FOREIGN KEY ([HostID]) REFERENCES [dbo].[__WWS__RemoteHosts] ([ID])
);

