namespace KNE;

/// <summary>Strict named lists and typed logic commands shared by code and visual blocks.</summary>
internal static class LogicRuntimeLua
{
    internal const string Source = """
        local lists = {}
        local keywords = {}; for word in ('and break do else elseif end false for function goto if in local nil not or repeat return then true until while'):gmatch('%S+') do keywords[word]=true end
        local function fail(message) error('Логика: '..message,3) end
        local function name_ok(name)
            if type(name)~='string' or #name==0 or #name>80 or not name:match('^[a-zA-Z_][a-zA-Z_0-9]*$') or name:match('^kne_') or keywords[name] then fail('недопустимое имя списка') end
        end
        local function clone(value, seen, depth)
            local kind=type(value)
            if kind=='number' then kne_math_value(value); return value end
            if kind=='string' or kind=='boolean' then return value end
            if kind~='table' then fail('значение должно быть числом, строкой, bool или таблицей') end
            if depth>16 then fail('глубина значения больше 16') end
            if seen[value] then fail('циклическое значение') end
            seen[value]=true
            local result, count, maximum, numeric, strings={},0,0,false,false
            for key,item in pairs(value) do
                count=count+1; if count>10000 then fail('таблица больше 10000 элементов') end
                if type(key)=='number' and key>=1 and key%1==0 and key<=10000 then numeric=true; maximum=math.max(maximum,key)
                elseif type(key)=='string' then strings=true else fail('недопустимый ключ таблицы') end
                result[key]=clone(item,seen,depth+1)
            end
            if numeric and (strings or maximum~=count) then fail('массив содержит пропуски или смешанные ключи') end
            seen[value]=nil; return result
        end
        local function array(value)
            if type(value)~='table' then fail('нужен список') end
            local count=0
            for key,_ in pairs(value) do
                if type(key)~='number' or key%1~=0 or key<1 then fail('список содержит недопустимый индекс') end
                count=count+1
            end
            if count>10000 then fail('список больше 10000 элементов') end
            for i=1,count do if value[i]==nil then fail('список содержит пропуски') end end
            return count
        end
        local function get(name)
            name_ok(name); local value=lists[name]
            if not value then fail('список не существует: '..name) end
            if rawget(_G,name)~=value then fail('глобальная ссылка на список изменена: '..name) end
            array(value); return value
        end
        local function index(value, n, insert)
            if type(value)~='number' or value%1~=0 or value<0 or value>=n+(insert and 1 or 0) then fail('индекс вне списка (индексы начинаются с 0)') end
            return value+1
        end
        local function equal(a,b)
            if type(a)~=type(b) then return false end
            if type(a)~='table' then return a==b end
            for k,v in pairs(a) do if not equal(v,b[k]) then return false end end
            for k,_ in pairs(b) do if a[k]==nil then return false end end
            return true
        end
        function create_list(name, values)
            name_ok(name); if lists[name] or rawget(_G,name)~=nil then fail('имя уже занято: '..name) end
            local value=values==nil and {} or clone(values,{},0); array(value)
            lists[name]=value; rawset(_G,name,value); return value
        end
        function list_exists(name) name_ok(name); return lists[name]~=nil end
        function delete_list(name) get(name); lists[name]=nil; rawset(_G,name,nil) end
        function clear_list(name) local value=get(name); for k,_ in pairs(value) do value[k]=nil end end
        function list_length(name) return array(get(name)) end
        function list_is_empty(name) return list_length(name)==0 end
        function list_get(name,i) local value=get(name); return value[index(i,#value,false)] end
        function list_set(name,i,item) local value=get(name); value[index(i,#value,false)]=clone(item,{},0) end
        function list_add(name,item) local value=get(name); if #value>=10000 then fail('список больше 10000 элементов') end; value[#value+1]=clone(item,{},0) end
        function list_insert(name,i,item) local value=get(name); local at=index(i,#value,true); if #value>=10000 then fail('список больше 10000 элементов') end; table.insert(value,at,clone(item,{},0)) end
        function list_remove_at(name,i) local value=get(name); return table.remove(value,index(i,#value,false)) end
        function list_index_of(name,item)
            local value=get(name); item=clone(item,{},0)
            for i,v in ipairs(value) do if equal(v,item) then return i-1 end end
            return -1
        end
        function list_contains(name,item) return list_index_of(name,item)>=0 end
        function list_remove(name,item) local i=list_index_of(name,item); if i<0 then return false end; list_remove_at(name,i); return true end
        function list_merge(name,other)
            local destination,source=get(name),clone(get(other),{},0)
            if #destination+#source>10000 then fail('список больше 10000 элементов') end
            for _,v in ipairs(source) do destination[#destination+1]=v end
        end
        function list_copy(name,other) local value=clone(get(name),{},0); if list_exists(other) then local target=get(other); clear_list(other); for _,v in ipairs(value) do target[#target+1]=v end else create_list(other,value) end end
        function list_shuffle(name) local value=get(name); for i=#value,2,-1 do local j=math.random(i); value[i],value[j]=value[j],value[i] end end
        function list_sort(name)
            local value=get(name); local kind=type(value[1]); if #value==0 then return end
            if kind~='string' and kind~='number' then fail('сортировка требует числа либо строки одного типа') end
            for _,v in ipairs(value) do if type(v)~=kind then fail('сортировка требует один тип данных') end; if kind=='number' then kne_math_value(v) end end
            table.sort(value)
        end
        function list_random(name) local value=get(name); if #value==0 then fail('случайный выбор из пустого списка') end; return value[math.random(#value)] end
        function kne_system_list(name,values)
            if not list_exists(name) then return create_list(name,values) end
            local target=get(name); local data=clone(values,{},0); array(data); clear_list(name)
            for i,v in ipairs(data) do target[i]=v end
            return target
        end
        function kne_list_export() for name,_ in pairs(lists) do get(name); clone(lists[name],{},0) end; return lists end
        function kne_list_restore(values) for name,_ in pairs(lists) do if values[name]==nil then delete_list(name) end end; for name,value in pairs(values) do if lists[name] then local target=get(name); local data=clone(value,{},0); array(data); clear_list(name); for i,v in ipairs(data) do target[i]=v end else create_list(name,value) end end end
        local function text(value) if type(value)~='string' then fail('операция требует строку') end; return value end
        function string_concat(a,b) return text(a)..text(b) end
        local function unicode(op,a,b,c)
            local result=kne_logic_text(op,text(a),b==nil and '' or text(b),c==nil and '' or text(c))
            if result[1]~='' then fail(result[1]) end; return result[0]
        end
        function string_length(a) return unicode('length',a) end
        function string_contains(a,b) return unicode('contains',a,b) end
        function string_replace(a,b,c) return unicode('replace',a,b,c) end
        function string_upper(a) return unicode('upper',a) end
        function string_lower(a) return unicode('lower',a) end
        function to_number(value) if type(value)~='string' and type(value)~='number' then fail('преобразование в число требует строку или число') end; local n=tonumber(value); if not n then fail('не удалось преобразовать в число') end; return kne_math_value(n) end
        function to_string(value) if type(value)~='string' and type(value)~='number' and type(value)~='boolean' then fail('преобразование в строку требует скалярное значение') end; if type(value)=='number' then kne_math_value(value) end; return tostring(value) end
        function to_bool(value) if value==true or value==1 or value=='true' or value=='1' then return true end; if value==false or value==0 or value=='false' or value=='0' then return false end; fail('преобразование в bool принимает true/false или 1/0') end
        function copy_variable(destination,source) if type(destination)~='string' or type(source)~='string' then fail('укажите имена переменных') end; if state[source]==nil then state[destination]=nil else state[destination]=clone(state[source],{},0) end end
        function reset_variable(name) if type(name)~='string' then fail('укажите имя переменной') end; local value=kne_logic_initial(name); local entry,problem=load('return '..value,'KNE variable initial','t',_G); if not entry then fail(problem) end; state[name]=entry() end
        function kne_logic_compare(op,a,b)
            if op=='==' then return a==b elseif op=='~=' then return a~=b end
            if type(a)~=type(b) or type(a)~='number' and type(a)~='string' then fail('сравнение требует числа либо строки одного типа') end
            if type(a)=='number' then kne_math_value(a); kne_math_value(b) end
            if op=='<' then return a<b elseif op=='>' then return a>b elseif op=='<=' then return a<=b elseif op=='>=' then return a>=b end
            fail('неизвестное сравнение')
        end
        """;
}
