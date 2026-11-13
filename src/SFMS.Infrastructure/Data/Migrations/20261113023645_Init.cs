using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SFMS.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alarm_event",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RuleId = table.Column<int>(type: "integer", nullable: true),
                    TagId = table.Column<int>(type: "integer", nullable: true),
                    DeviceId = table.Column<int>(type: "integer", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TriggerValue = table.Column<double>(type: "double precision", nullable: true),
                    Message = table.Column<string>(type: "text", nullable: false),
                    AckBy = table.Column<string>(type: "text", nullable: true),
                    AckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AckNote = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alarm_event", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "app_user",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    RoleCode = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_user", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "confirm_token",
                columns: table => new
                {
                    Token = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    PayloadHash = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_confirm_token", x => x.Token);
                });

            migrationBuilder.CreateTable(
                name: "endpoint",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Protocol = table.Column<int>(type: "integer", nullable: false),
                    Ip = table.Column<string>(type: "text", nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    ConnParams = table.Column<string>(type: "text", nullable: false),
                    ScanEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastOkAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Remark = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_endpoint", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "import_job",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Valid = table.Column<int>(type: "integer", nullable: false),
                    Invalid = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_job", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "scan_group",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PeriodMs = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    MergeGap = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scan_group", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "screen",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    PublishedVersionId = table.Column<int>(type: "integer", nullable: true),
                    PublishedVersionNo = table.Column<int>(type: "integer", nullable: false),
                    DraftJson = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_screen", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "screen_role",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScreenId = table.Column<int>(type: "integer", nullable: false),
                    RoleCode = table.Column<string>(type: "text", nullable: false),
                    CanView = table.Column<bool>(type: "boolean", nullable: false),
                    CanEdit = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_screen_role", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "screen_version",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScreenId = table.Column<int>(type: "integer", nullable: false),
                    VersionNo = table.Column<int>(type: "integer", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_screen_version", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "system_setting",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_setting", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "tag_latest",
                columns: table => new
                {
                    TagId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Value = table.Column<double>(type: "double precision", nullable: true),
                    Text = table.Column<string>(type: "text", nullable: true),
                    Quality = table.Column<int>(type: "integer", nullable: false),
                    Ts = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag_latest", x => x.TagId);
                });

            migrationBuilder.CreateTable(
                name: "tag_runtime_state",
                columns: table => new
                {
                    TagId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LastStoredValue = table.Column<double>(type: "double precision", nullable: true),
                    LastStoredText = table.Column<string>(type: "text", nullable: true),
                    LastStoredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastQuality = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag_runtime_state", x => x.TagId);
                });

            migrationBuilder.CreateTable(
                name: "write_batch",
                columns: table => new
                {
                    BatchId = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_write_batch", x => x.BatchId);
                });

            migrationBuilder.CreateTable(
                name: "device",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EndpointId = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DeviceKey = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: true),
                    Workshop = table.Column<string>(type: "text", nullable: true),
                    Area = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Paused = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device", x => x.Id);
                    table.ForeignKey(
                        name: "FK_device_endpoint_EndpointId",
                        column: x => x.EndpointId,
                        principalTable: "endpoint",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_staging",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JobId = table.Column<int>(type: "integer", nullable: false),
                    RowNo = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    ErrorText = table.Column<string>(type: "text", nullable: true),
                    IsValid = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_import_staging", x => x.Id);
                    table.ForeignKey(
                        name: "FK_import_staging_import_job_JobId",
                        column: x => x.JobId,
                        principalTable: "import_job",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "write_batch_item",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchId = table.Column<string>(type: "text", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    OldValue = table.Column<double>(type: "double precision", nullable: true),
                    NewValue = table.Column<double>(type: "double precision", nullable: true),
                    ReadBack = table.Column<double>(type: "double precision", nullable: true),
                    Ok = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_write_batch_item", x => x.Id);
                    table.ForeignKey(
                        name: "FK_write_batch_item_write_batch_BatchId",
                        column: x => x.BatchId,
                        principalTable: "write_batch",
                        principalColumn: "BatchId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tag",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AddressExpr = table.Column<string>(type: "text", nullable: false),
                    Area = table.Column<int>(type: "integer", nullable: false),
                    AreaNo = table.Column<int>(type: "integer", nullable: false),
                    ByteOffset = table.Column<int>(type: "integer", nullable: false),
                    BitOffset = table.Column<int>(type: "integer", nullable: false),
                    DataType = table.Column<int>(type: "integer", nullable: false),
                    ByteOrder = table.Column<int>(type: "integer", nullable: false),
                    StrLen = table.Column<int>(type: "integer", nullable: false),
                    Writable = table.Column<bool>(type: "boolean", nullable: false),
                    MinValue = table.Column<double>(type: "double precision", nullable: true),
                    MaxValue = table.Column<double>(type: "double precision", nullable: true),
                    Scale = table.Column<double>(type: "double precision", nullable: false),
                    Offset = table.Column<double>(type: "double precision", nullable: false),
                    Unit = table.Column<string>(type: "text", nullable: true),
                    ScanGroupId = table.Column<int>(type: "integer", nullable: false),
                    Deadband = table.Column<double>(type: "double precision", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    AddressKey = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tag", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tag_device_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "device",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tag_scan_group_ScanGroupId",
                        column: x => x.ScanGroupId,
                        principalTable: "scan_group",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alarm_rule",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TagId = table.Column<int>(type: "integer", nullable: true),
                    EndpointId = table.Column<int>(type: "integer", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    Hysteresis = table.Column<double>(type: "double precision", nullable: false),
                    OnDelayMs = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alarm_rule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alarm_rule_tag_TagId",
                        column: x => x.TagId,
                        principalTable: "tag",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_alarm_event_State",
                table: "alarm_event",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_alarm_event_TagId_StartedAt",
                table: "alarm_event",
                columns: new[] { "TagId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_alarm_rule_EndpointId",
                table: "alarm_rule",
                column: "EndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_alarm_rule_TagId",
                table: "alarm_rule",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_app_user_Username",
                table: "app_user",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_EndpointId_Code",
                table: "device",
                columns: new[] { "EndpointId", "Code" },
                unique: true,
                filter: "NOT \"IsDeleted\"");

            migrationBuilder.CreateIndex(
                name: "IX_device_EndpointId_DeviceKey",
                table: "device",
                columns: new[] { "EndpointId", "DeviceKey" },
                filter: "NOT \"IsDeleted\"");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_Code",
                table: "endpoint",
                column: "Code",
                unique: true,
                filter: "NOT \"IsDeleted\"");

            migrationBuilder.CreateIndex(
                name: "IX_endpoint_Ip_Port",
                table: "endpoint",
                columns: new[] { "Ip", "Port" },
                filter: "NOT \"IsDeleted\"");

            migrationBuilder.CreateIndex(
                name: "IX_import_staging_JobId",
                table: "import_staging",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_scan_group_Name",
                table: "scan_group",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_screen_Code",
                table: "screen",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_screen_role_ScreenId_RoleCode",
                table: "screen_role",
                columns: new[] { "ScreenId", "RoleCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_screen_version_ScreenId_VersionNo",
                table: "screen_version",
                columns: new[] { "ScreenId", "VersionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tag_DeviceId_AddressKey",
                table: "tag",
                columns: new[] { "DeviceId", "AddressKey" },
                filter: "NOT \"IsDeleted\"");

            migrationBuilder.CreateIndex(
                name: "IX_tag_DeviceId_Code",
                table: "tag",
                columns: new[] { "DeviceId", "Code" },
                unique: true,
                filter: "NOT \"IsDeleted\"");

            migrationBuilder.CreateIndex(
                name: "IX_tag_ScanGroupId",
                table: "tag",
                column: "ScanGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_write_batch_item_BatchId",
                table: "write_batch_item",
                column: "BatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alarm_event");

            migrationBuilder.DropTable(
                name: "alarm_rule");

            migrationBuilder.DropTable(
                name: "app_user");

            migrationBuilder.DropTable(
                name: "confirm_token");

            migrationBuilder.DropTable(
                name: "import_staging");

            migrationBuilder.DropTable(
                name: "screen");

            migrationBuilder.DropTable(
                name: "screen_role");

            migrationBuilder.DropTable(
                name: "screen_version");

            migrationBuilder.DropTable(
                name: "system_setting");

            migrationBuilder.DropTable(
                name: "tag_latest");

            migrationBuilder.DropTable(
                name: "tag_runtime_state");

            migrationBuilder.DropTable(
                name: "write_batch_item");

            migrationBuilder.DropTable(
                name: "tag");

            migrationBuilder.DropTable(
                name: "import_job");

            migrationBuilder.DropTable(
                name: "write_batch");

            migrationBuilder.DropTable(
                name: "device");

            migrationBuilder.DropTable(
                name: "scan_group");

            migrationBuilder.DropTable(
                name: "endpoint");
        }
    }
}
