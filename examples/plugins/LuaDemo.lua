-- Registration runs in editor/player as well as in the game worker.
-- Keep state mutations inside execution callbacks and runtime events.
function sdk_lua_counter(parameters_json)
    state.lua_counter = (state.lua_counter or 0) + 1
    return 1
end

function sdk_lua_text(text, speaker)
    return text:gsub("Hello", "Привет"), speaker
end

kne.register_group([[{"id":"lua_demo","title":"Lua SDK","color":"#2B86B8","icon":"☾"}]])
kne.register_block([[{
  "id":"lua_demo.counter","title":"Счётчик Lua","group":"lua_demo",
  "description":"Увеличить state.lua_counter на один","icon":"☾","color":"#2B86B8",
  "inputs":["Вход"],"outputs":["Далее"]
}]], "sdk_lua_counter")
kne.before_text("sdk_lua_text")
