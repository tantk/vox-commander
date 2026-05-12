from vox.refs import RefResolver


def test_passthrough_unknown_ref_kept_as_is():
    r = RefResolver()
    out = r.rewrite({"target": "east_edge"})
    assert out == {"target": "east_edge"}


def test_that_base_resolves_to_most_recent_enemy_structure_in_snapshot():
    r = RefResolver()
    r.ingest_snapshot({
        "kind": "state_snapshot",
        "enemies": [
            {"handle": "enemy_barracks_alpha", "kind": "barracks", "owner": "enemy"},
            {"handle": "enemy_factory_beta",   "kind": "factory",  "owner": "enemy"},
        ],
    })
    out = r.rewrite({"target_ref": "that_base"})
    assert out == {"target_ref": "enemy_factory_beta"}  # most recent in list


def test_pronoun_kind_resolves_via_kind_field():
    r = RefResolver()
    r.ingest_snapshot({
        "kind": "state_snapshot",
        "enemies": [{"handle": "enemy_harvester_01", "kind": "harvester", "owner": "enemy"}],
    })
    out = r.rewrite({"target_kind": "harvester"})
    assert out == {"target_kind": "enemy_harvester"}


def test_ambiguous_that_returns_marker_for_xo_clarification():
    r = RefResolver()
    out = r.rewrite({"target_ref": "that_base"})
    assert out == {"target_ref": "__ambiguous__"}
