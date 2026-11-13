-- SFMS 时序/审计/诊断表（按月 RANGE 分区，等价实现 TimescaleDB 的 hypertable + retention）
-- 由 DbBootstrapper 在启动时幂等执行；配置域表由 EF Core 迁移负责创建。
-- 如生产环境可安装 TimescaleDB，另见 090_timescale_optional.sql（仅空库初始化时使用）。

CREATE SEQUENCE IF NOT EXISTS audit_log_id_seq;

-- ---------------------------------------------------------------- 原始历史
CREATE TABLE IF NOT EXISTS tag_history (
    tag_id      integer      NOT NULL,
    ts          timestamptz  NOT NULL,
    value_num   double precision,
    value_text  text,
    quality     smallint     NOT NULL DEFAULT 0,
    seq         bigint       NOT NULL DEFAULT 0,
    PRIMARY KEY (tag_id, ts)
) PARTITION BY RANGE (ts);

-- ---------------------------------------------------------------- 1 分钟聚合
CREATE TABLE IF NOT EXISTS tag_agg_1m (
    tag_id        integer      NOT NULL,
    bucket        timestamptz  NOT NULL,
    min_value     double precision,
    max_value     double precision,
    avg_value     double precision,
    first_value   double precision,
    last_value    double precision,
    cnt           integer      NOT NULL DEFAULT 0,
    worst_quality smallint     NOT NULL DEFAULT 0,
    PRIMARY KEY (tag_id, bucket)
) PARTITION BY RANGE (bucket);

-- ---------------------------------------------------------------- 审计日志
CREATE TABLE IF NOT EXISTS audit_log (
    "Id"        bigint      NOT NULL DEFAULT nextval('audit_log_id_seq'),
    "Ts"        timestamptz NOT NULL,
    "UserId"    integer,
    "Username"  text,
    "Action"    text        NOT NULL,
    "TargetType" text,
    "TargetId"  text,
    "Before"    text,
    "After"     text,
    "Result"    text        NOT NULL DEFAULT 'Success',
    "Reason"    text,
    "Ip"        text,
    "TraceId"   text,
    PRIMARY KEY ("Id", "Ts")
) PARTITION BY RANGE ("Ts");

-- ---------------------------------------------------------------- 端点分钟诊断
CREATE TABLE IF NOT EXISTS endpoint_diag_minute (
    bucket      timestamptz NOT NULL,
    endpoint_id integer     NOT NULL,
    requests    bigint      NOT NULL DEFAULT 0,
    success     bigint      NOT NULL DEFAULT 0,
    timeouts    bigint      NOT NULL DEFAULT 0,
    errors      bigint      NOT NULL DEFAULT 0,
    avg_ms      double precision NOT NULL DEFAULT 0,
    p95_ms      double precision NOT NULL DEFAULT 0,
    error_codes text,
    PRIMARY KEY (bucket, endpoint_id)
) PARTITION BY RANGE (bucket);

-- ---------------------------------------------------------------- 分区维护
CREATE OR REPLACE FUNCTION sfms_ensure_partitions(months_ahead integer DEFAULT 3)
RETURNS integer AS $$
DECLARE
    t record;
    m integer;
    start_ts timestamptz;
    end_ts timestamptz;
    part_name text;
    created integer := 0;
BEGIN
    PERFORM pg_advisory_xact_lock(918273645);
    FOR t IN
        SELECT * FROM (VALUES
            ('tag_history', 'ts'),
            ('tag_agg_1m', 'bucket'),
            ('audit_log', '"Ts"'),
            ('endpoint_diag_minute', 'bucket')
        ) AS x(tbl, col)
    LOOP
        FOR m IN -1..months_ahead LOOP
            start_ts := date_trunc('month', now()) + (m || ' month')::interval;
            end_ts   := start_ts + interval '1 month';
            part_name := format('%s_%s', t.tbl, to_char(start_ts, 'YYYYMM'));
            IF to_regclass(part_name) IS NULL THEN
                EXECUTE format('CREATE TABLE %I PARTITION OF %I FOR VALUES FROM (%L) TO (%L)',
                               part_name, t.tbl, start_ts, end_ts);
                IF t.tbl = 'tag_history' THEN
                    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %I USING BRIN (ts)',
                                   part_name || '_ts_brin', part_name);
                END IF;
                created := created + 1;
            END IF;
        END LOOP;
    END LOOP;
    RETURN created;
END;
$$ LANGUAGE plpgsql;

CREATE OR REPLACE FUNCTION sfms_drop_old_partitions(months_keep integer DEFAULT 3, audit_months integer DEFAULT 12)
RETURNS integer AS $$
DECLARE
    r record;
    dropped integer := 0;
    cutoff timestamptz;
BEGIN
    FOR r IN
        SELECT c.relname AS child, p.relname AS parent
        FROM pg_inherits i
        JOIN pg_class c ON c.oid = i.inhrelid
        JOIN pg_class p ON p.oid = i.inhparent
        WHERE p.relname IN ('tag_history', 'tag_agg_1m', 'audit_log', 'endpoint_diag_minute')
    LOOP
        cutoff := date_trunc('month', now()) - ((CASE WHEN r.parent = 'audit_log' THEN audit_months ELSE months_keep END) || ' month')::interval;
        IF r.child ~ '[0-9]{6}$'
           AND to_date(right(r.child, 6), 'YYYYMM') + interval '1 month' <= cutoff THEN
            EXECUTE format('DROP TABLE IF EXISTS %I', r.child);
            dropped := dropped + 1;
        END IF;
    END LOOP;
    RETURN dropped;
END;
$$ LANGUAGE plpgsql;

-- ---------------------------------------------------------------- 1 分钟聚合（幂等）
CREATE OR REPLACE FUNCTION sfms_rollup_minute(p_bucket timestamptz)
RETURNS integer AS $$
DECLARE
    affected integer;
BEGIN
    DELETE FROM tag_agg_1m WHERE bucket = p_bucket;
    INSERT INTO tag_agg_1m (tag_id, bucket, min_value, max_value, avg_value, first_value, last_value, cnt, worst_quality)
    SELECT tag_id,
           p_bucket,
           min(value_num),
           max(value_num),
           avg(value_num),
           (array_agg(value_num ORDER BY ts))[1],
           (array_agg(value_num ORDER BY ts DESC))[1],
           count(*),
           max(quality)
    FROM tag_history
    WHERE ts >= p_bucket AND ts < p_bucket + interval '1 minute'
      AND value_num IS NOT NULL
    GROUP BY tag_id;
    GET DIAGNOSTICS affected = ROW_COUNT;
    RETURN affected;
END;
$$ LANGUAGE plpgsql;

SELECT sfms_ensure_partitions(3);
