-- 可选的 TimescaleDB 路径（仅空库初始化时使用；已建普通分区表时不可原位转换）
-- 用法：在安装 timescaledb 扩展的实例上，先执行本脚本再启动应用，并把
-- appsettings 中的 Database:UseTimescale 置为 true（应用将跳过分区函数调用）。
CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE IF NOT EXISTS tag_history (
    tag_id      integer      NOT NULL,
    ts          timestamptz  NOT NULL,
    value_num   double precision,
    value_text  text,
    quality     smallint     NOT NULL DEFAULT 0,
    seq         bigint       NOT NULL DEFAULT 0,
    PRIMARY KEY (tag_id, ts)
);
SELECT create_hypertable('tag_history', 'ts', chunk_time_interval => interval '1 day', if_not_exists => TRUE);
SELECT add_retention_policy('tag_history', interval '3 months', if_not_exists => TRUE);

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
);
SELECT create_hypertable('tag_agg_1m', 'bucket', chunk_time_interval => interval '7 days', if_not_exists => TRUE);
SELECT add_retention_policy('tag_agg_1m', interval '36 months', if_not_exists => TRUE);
