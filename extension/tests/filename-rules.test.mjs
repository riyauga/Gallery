import test from "node:test";
import assert from "node:assert/strict";
import {matchFilename} from "../filename-rules.js";

test("first matching filename rule maps captures to Gallery categories", () => {
  const rules = [
    {pattern:"^(.+)_(.+)\\.jpg$", groups:{"1":"artist","2":"character"}},
    {pattern:"^(.*)$", groups:{"1":"custom"}}
  ];
  assert.deepEqual(matchFilename(rules, "artist_character.jpg"), {artist:["artist"],character:["character"]});
  assert.equal(matchFilename(rules.slice(0,1), "other.png"), null);
  assert.deepEqual(matchFilename([{pattern:"[",groups:{}},rules[1]],"other.png"), {custom:["other.png"]});
});
